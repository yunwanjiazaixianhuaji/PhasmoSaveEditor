using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PhasmoSaveEditor.Core;

/// <summary>
/// Easy Save 3 存档加解密。
///
/// 文件布局：IV(16 字节) || AES-128-CBC(PKCS7(明文))
/// 密钥派生：PBKDF2-HMAC-SHA1(密码, salt = IV, 迭代 100 轮, 输出 16 字节)
///
/// 关键点：salt 就是 IV 本身（不是两个独立的值）。
/// 密码是 Easy Save 3 的默认密码，游戏作者没有改过，
/// 明文放在 resources.assets 的 ES3Defaults 资产里。
/// </summary>
public static class SaveCrypto
{
    public const int IvLen = 16;
    public const int KeyLen = 16;      // AES-128，不是 256
    public const int Iterations = 100;

    /// <summary>Easy Save 3 默认密码。</summary>
    public static readonly byte[] Password = Encoding.ASCII.GetBytes("t36gref9u84y7f43g");

    /// <summary>用 IV 同时作为 salt 派生 AES 密钥。</summary>
    public static byte[] DeriveKey(byte[] iv)
        => Rfc2898DeriveBytes.Pbkdf2(Password, iv, Iterations, HashAlgorithmName.SHA1, KeyLen);

    private static Aes NewAes(byte[] iv)
    {
        var aes = Aes.Create();
        aes.KeySize = KeyLen * 8;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = DeriveKey(iv);
        aes.IV = iv;
        return aes;
    }

    /// <summary>解密整个存档文件，返回明文字节。</summary>
    public static byte[] DecryptBytes(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length < IvLen * 2 || (data.Length - IvLen) % 16 != 0)
            throw new InvalidDataException(
                $"文件不是 IV + AES-CBC 结构（{data.Length} 字节）。请确认选中的是 SaveFile.txt。");

        var iv = new byte[IvLen];
        Buffer.BlockCopy(data, 0, iv, 0, IvLen);

        int ctLen = data.Length - IvLen;
        using var aes = NewAes(iv);
        using var dec = aes.CreateDecryptor();
        try
        {
            return dec.TransformFinalBlock(data, IvLen, ctLen);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException(
                "PKCS7 填充非法 —— 密码不符、存档版本变化，或文件已损坏。");
        }
    }

    /// <summary>加密明文，返回 IV + 密文。</summary>
    public static byte[] EncryptBytes(byte[] plaintext, byte[] iv = null)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
        iv ??= RandomNumberGenerator.GetBytes(IvLen);
        if (iv.Length != IvLen) throw new ArgumentException("IV 必须是 16 字节", nameof(iv));

        using var aes = NewAes(iv);
        using var enc = aes.CreateEncryptor();
        var ct = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var outBuf = new byte[IvLen + ct.Length];
        Buffer.BlockCopy(iv, 0, outBuf, 0, IvLen);
        Buffer.BlockCopy(ct, 0, outBuf, IvLen, ct.Length);
        return outBuf;
    }

    // 用不带 BOM 的 UTF-8：若原文自带 BOM，解码后是 \uFEFF 字符，
    // 再编码回去仍是同样的 BOM 字节，往返不会丢失或凭空增加。
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string DecryptToText(byte[] data) => Utf8.GetString(DecryptBytes(data));

    public static byte[] EncryptText(string text) => EncryptBytes(Utf8.GetBytes(text ?? ""));

    /// <summary>明文的 UTF-8 字节数（不含加密开销），用于校验与状态栏显示。</summary>
    public static int Utf8Len(string text) => Utf8.GetByteCount(text ?? "");

    /// <summary>读取并解密存档文件，返回明文文本。</summary>
    public static string ReadSaveText(string path) => DecryptToText(File.ReadAllBytes(path));

    /// <summary>
    /// 加密回读自检：确认写出去的密文能原样解回来。
    /// 这是写盘前的最后一道闸，防止把存档写坏。
    /// </summary>
    public static bool RoundTripOk(string text, out byte[] blob)
    {
        blob = EncryptText(text);
        try
        {
            return DecryptToText(blob) == text;
        }
        catch
        {
            return false;
        }
    }
}
