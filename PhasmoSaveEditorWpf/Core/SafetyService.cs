using System.Diagnostics;
using System.IO;

namespace PhasmoSaveEditor.Core;

/// <summary>
/// 写盘前的全部保护机制。
///
/// 三道闸按顺序执行，任一道不过就不写盘：
///   1. 游戏进程检测 —— 游戏运行时改动无效（退出时会用内存里的旧存档覆盖磁盘）
///   2. 时间戳备份   —— 改坏了可以改名换回去
///   3. 加密回读自检 —— 确认写出去的密文能原样解回来
/// </summary>
public static class SafetyService
{
    /// <summary>游戏进程名（不含扩展名）。</summary>
    public const string GameProcessName = "Phasmophobia";

    /// <summary>
    /// 默认存档路径。注意存档**不在** Steam 游戏根目录，
    /// 而在 AppData\LocalLow 底下（Unity 的规矩）。
    /// </summary>
    public static string DefaultSavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Kinetic Games", "Phasmophobia", "SaveFile.txt");

    /// <summary>检测游戏是否正在运行。检测失败时保守返回 false，不阻塞用户。</summary>
    public static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName(GameProcessName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 备份原存档为 SaveFile.txt.bak-yyyyMMdd-HHmmss，返回备份路径。
    /// </summary>
    public static string Backup(string savePath)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string backup = savePath + ".bak-" + stamp;
        // 同一秒内连续写入时避免覆盖上一份备份
        int n = 1;
        while (File.Exists(backup))
            backup = $"{savePath}.bak-{stamp}-{n++}";
        File.Copy(savePath, backup, overwrite: false);
        return backup;
    }

    /// <summary>写盘的结果描述。</summary>
    public sealed record WriteResult(string BackupPath, int PlainBytes, int FileBytes);

    /// <summary>
    /// 完整的安全写入流程。抛异常即代表没有写盘（原存档未受影响）。
    /// </summary>
    /// <param name="savePath">目标存档路径</param>
    /// <param name="newText">新的明文内容</param>
    /// <param name="allowWhileGameRunning">
    /// 通常为 false。仅当用户已在 UI 上明确确认「我知道会被覆盖，仍要写」时才传 true。
    /// </param>
    public static WriteResult WriteSave(string savePath, string newText,
                                        bool allowWhileGameRunning = false)
    {
        if (string.IsNullOrWhiteSpace(savePath))
            throw new ArgumentException("未指定存档路径", nameof(savePath));
        if (!File.Exists(savePath))
            throw new FileNotFoundException("找不到存档文件：\n" + savePath, savePath);

        // 闸 1：进程检测
        if (!allowWhileGameRunning && IsGameRunning())
            throw new InvalidOperationException(GameRunningMessage);

        // 闸 3 前置：先做加密回读自检，通过了才动磁盘上的任何文件
        if (!SaveCrypto.RoundTripOk(newText, out byte[] blob))
            throw new InvalidOperationException(
                "加密自检失败：重新解密后与要写入的内容不一致。\n" +
                "为防止写坏存档，已放弃写入，原存档未受影响。");

        // 闸 2：备份（自检通过后才备份，避免产生无意义备份）
        string backup = Backup(savePath);

        // 原子写：先写临时文件再替换，避免写一半断电留下半个存档
        string tmp = savePath + ".tmp-writing";
        File.WriteAllBytes(tmp, blob);
        File.Copy(tmp, savePath, overwrite: true);
        File.Delete(tmp);

        return new WriteResult(backup, System.Text.Encoding.UTF8.GetByteCount(newText), blob.Length);
    }

    public const string GameRunningMessage =
        "检测到游戏正在运行，已拒绝写入。\n\n" +
        "请先完全退出游戏再修改 —— 游戏开着时改是没用的，\n" +
        "退出游戏那一下它会把内存里的旧存档写回磁盘，正好覆盖你的修改。";

    /// <summary>
    /// 从备份目录里列出最近的备份，方便用户一键回滚。
    /// </summary>
    public static IReadOnlyList<string> ListBackups(string savePath, int max = 10)
    {
        try
        {
            string dir = Path.GetDirectoryName(savePath);
            string name = Path.GetFileName(savePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return Array.Empty<string>();

            return Directory.GetFiles(dir, name + ".bak-*")
                .OrderByDescending(p => File.GetLastWriteTime(p))
                .Take(max)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
