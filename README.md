# 恐鬼症存档修改器（WPF 版）

改恐鬼症（Phasmophobia）本地存档的 Windows 桌面工具。

能改存档里的**全部 597 个字段**：金钱、等级、转生、21 种装备的阶级与库存、塔罗牌、每日/每周挑战进度、统计数据、成就、角色外观与配色。颜色、字典、列表、外观、衣柜、难度对象这些复杂类型都做了专用可视化编辑器，不是丢个文本框让你手撸 JSON。

这是 [纯 Python 版](#致谢与上游) 的重做版。原版只能改金钱和等级，且**在本机因缺少 Python 环境而无法运行**；重做后改用 .NET 9 WPF，发布为免安装单文件 exe。

---

## 下载

到 [Releases](../../releases) 页面下载 `PhasmoSaveEditor.exe`，双击即用，**不需要装 .NET**（自包含单文件，约 120 MB）。

exe 不放在仓库里——它超过了 GitHub 单文件 100 MB 的上限，只能通过 Releases 分发。想自己构建见下方「从源码构建」。

---

## 使用要点

**改之前必须完全退出游戏。** 游戏开着时改了没用——退出那一下它会把内存里的旧存档写回去，正好覆盖你的修改。程序会检测 `Phasmophobia` 进程，检测到就拒绝写入。

存档路径会自动定位：

```
%USERPROFILE%\AppData\LocalLow\Kinetic Games\Phasmophobia\SaveFile.txt
```

注意存档**不在** Steam 游戏根目录，在 `AppData\LocalLow` 底下（Unity 的规矩）。找不到可以在界面上手动选。

写入前自动生成带时间戳的备份 `SaveFile.txt.bak-20261023-041522`，改坏了把备份改名换回去就行。

完整的操作说明、预设规则、失效排查见 [PhasmoSaveEditorWpf/使用说明.md](PhasmoSaveEditorWpf/使用说明.md)。

---

## ⚠️ 关于等级：上游的结论已过时

上游 Python 版的说明里写的是「存档里**没有**等级字段，等级是游戏拿 `Experience` 现算的」，并给出了 `floor(100 × (等级−1)^1.73)` 这个换算公式。

**这个结论在当前游戏版本已经错了。** 实测一份真实存档，同时存在：

```
Level          = 101
NewLevel       = 75
Experience     = 177
myTotalExp     = 100
Prestige       = 5
```

如果 `Experience` 真是累计经验，177 按那个公式只够 2 级，可 `Level` 是 101、`Prestige` 是 5 —— 明显对不上。

交叉核对游戏自己渲染的身份卡（存档目录下的 `IDCardRender0.png`，印着 `V-075` 和 `177/4024`）以及游戏日志后确认：

| 字段 | 真实含义 | 证据 |
|---|---|---|
| `NewLevel` | **玩家当前显示等级** | 身份卡印 `V-075` ↔ 存档 `75` |
| `Prestige` | 转生次数（身份卡前缀的罗马数字） | `V` = 5 ↔ 存档 `5` |
| `Experience` | **当前等级内的进度经验**，不是累计 | 身份卡印 `177/4024` ↔ 存档 `177` |
| `Level` | 旧版遗留字段，当前版本不用于显示 | 日志中的 `Legacy Level` |
| `myTotalExp` | 未能确认，两个同类项目都不读写它 | — |

所以本工具改等级时**直接写 `NewLevel`**（范围 1~9999）、改转生**直接写 `Prestige`**（范围 0~3999），不再走经验换算公式。这也和 GitHub 上另两个活跃的同类项目做法一致（[BitterG/Phasmophobia-Save-Editor](https://github.com/BitterG/Phasmophobia-Save-Editor)、[stth12/PhasmoEditor](https://github.com/stth12/PhasmoEditor)）。

---

## 存档的加密方式

```
文件 = IV(16 字节) || AES-128-CBC(PKCS7(明文))

key = PBKDF2-HMAC-SHA1(密码, salt = IV, 迭代 100 轮, 输出 16 字节)
```

密码是 `t36gref9u84y7f43g`，在 `resources.assets` 的 `ES3Defaults` 资产里明文放着。这是 Easy Save 3 插件的默认密码，游戏作者没改过。

**关键是 salt 就是 IV 本身** —— 挺反直觉的，一般都当两个独立的东西。这一点是上游读反汇编定位到的，不是猜的；逆向过程（Il2CppDumper + capstone 手撸反汇编）和走过的弯路记录在上游 README 里，见下方致谢。

本仓库的实现在 [`PhasmoSaveEditorWpf/Core/SaveCrypto.cs`](PhasmoSaveEditorWpf/Core/SaveCrypto.cs)，用 .NET 内置加密，逻辑与上游手写 AES 完全一致。

---

## 非标准 JSON：为什么不能直接反序列化

存档是 Easy Save 3 的 JSON，但**不是标准 JSON**，`System.Text.Json` 直接吃不下：

```jsonc
"playedMaps" : { "__type":"System.Collections.Generic.Dictionary`2[...]", "value": {11:166,0:154} }
//                                                                            ^^ 裸整数 key，没有引号

"LocalPlayerOutfit" : { "value": { "Items" : {{"low":1,"high":0}:{"guid":{...},"materialOption":0}} } }
//                                              ^^^^^^^^^^^^^^^ 用对象当字典 key
```

所以采用「**定位 + 原地替换**」策略（[`Core/SaveDocument.cs`](PhasmoSaveEditorWpf/Core/SaveDocument.cs)）：

- 基础类型（int / bool / float / string）用正则定位后替换 value 片段
- 复杂类型用括号配平提取整个 value 块，交给 `Complex/` 下的专用解析器
- **未修改的字段写回时逐字节还原原文**，不靠格式复刻

最后一条是核心安全设计。Easy Save 3 的排版细节很多（TAB 缩进、字典条目无空格、CRLF 换行、`itemTiers` 是含空白字符的空数组 `[\n\t\t\t\n\t\t]`、衣柜条目分隔是 `},{` 逗号后紧跟开括号），复刻一处就少一个字节。因此 `ComplexValueBase` 保存 `OriginalRaw`，`IsModified == false` 时直接返回原文 —— 没碰过的字段不可能被写坏。

### 字段名里的坑

字段名**不能**用简单的通配匹配，必须逐个 `Regex.Escape`（存在 `SoundRecorder-1Tier`、`AutoBuy-1`、`daily-1Progression`、`10 Ridgeview CourtEaster2025Complete` 这类含连字符、空格、数字开头的 key）。

还有一个容易写错的：存档里 `Recieved`（拼写错误）和 `Received`（正确拼写）**两种同时存在**，批量匹配挑战完成状态时必须都覆盖，否则一半挑战改不到。

---

## 从源码构建

需要 [.NET SDK 9.0](https://dotnet.microsoft.com/download) 或更高。仓库**不依赖任何 NuGet 包**（MVVM 基础设施是手写的），所以离线也能构建。

```bash
# 调试构建
dotnet build PhasmoSaveEditorWpf/PhasmoSaveEditorWpf.csproj -c Debug

# 发布免安装单文件 exe（输出到 dist/）
dotnet publish PhasmoSaveEditorWpf/PhasmoSaveEditorWpf.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -o dist
```

### 跑测试

两个测试工程都在 `tests/`，**只在存档副本上操作，不会碰你的真实存档**：

```bash
dotnet run --project tests/CoreTests/CoreTests.csproj    # 加解密 / 597 字段枚举 / 复杂类型往返
dotnet run --project tests/E2ETests/E2ETests.csproj      # 改动生效 / 逐字节不变 / 完整写入链路
```

测试需要一个真实的加密存档作为输入（默认读 `%USERPROFILE%\AppData\LocalLow\Kinetic Games\Phasmophobia\SaveFile.txt`）。当前断言数：**CoreTests 245 项 + E2ETests 84 项**。

改完加密逻辑或解析器务必重跑这两个——历史上出过 5 个「编译通过但会写坏存档」的问题，全靠它们兜住：

| 问题 | 后果 | 怎么被发现的 |
|---|---|---|
| bool 写成 `True`/`False` | 151 个字段格式非法 | 原值回写测试长度不一致 |
| float 用 `float` 承载 | `6703.72168` 丢精度 | 存档有 9 位有效数字，超出 float 的 7 位 |
| 复杂块用 LF 换行 | 每行少 1 字节 | Outfit 差 86 字节，全部来自换行 |
| 衣柜条目排版多缩进 | 差 33 个 TAB | 字节数对比 |
| XAML 资源字典合并顺序 | Release 单文件下**栈溢出崩溃**（`0xc00000fd`） | 加了 `--diag` 分步落盘诊断才定位到 |

---

## 代码结构

```
PhasmoSaveEditorWpf/
├── Core/         存档加解密、非标准 JSON 字段读写、写入保护（进程检测/备份/自检）
├── Schema/       597 字段的分类规则、中文显示名、装备定义、取值范围
├── Complex/      Color / Dict / List / Outfit / Wardrobe / Difficulty 双向解析器
├── ViewModels/   MVVM 基础设施、字段视图模型、主界面逻辑、四个一键预设
└── Views/        主窗口、各字段类型的编辑模板、值转换器
tests/
├── CoreTests/    加解密与字段读写的单元级验证
└── E2ETests/     端到端：完整修改→写入→重新解密核对
```

字段分类采用**规则式匹配**（正则 + 装备表驱动）而非手写 597 条映射，游戏更新新增字段时能自动归位，未命中的兜底进「高级」分组。当前中文名覆盖率 **100%**。

### 四个一键预设

1. **解锁全装备 + 全三阶** —— 所有 `*UnlockOwned` / `*Unlocked` → true，`*Tier` → 3
2. **金钱拉满 + 库存拉满** —— 金钱 → 999999999，库存 → 99（安全值，不用 int 上限以免游戏内异常）
3. **设定等级 / 转生** —— 直接写 `NewLevel` 与 `Prestige`
4. **全成就 / 挑战完成** —— 覆盖 `ACH_*` 与 `daily*` / `weekly*`

预设遵守两条安全规则：

- **只升不降**：实测 `lighthouseKeeperCompleted = 50`（不是 1），若盲目把所有 `*Completed` 置 1 会把它**降级**。
- **精确匹配装备名**：不用通配，否则 `Bone0`、`daily0` 会被误伤。

预设只改内存中的值，仍需点「应用修改」并确认才写盘。

---

## 已知问题

- **云存档会覆盖。** 游戏接了 Unity CloudSave，Steam 云同步也可能开着。若进游戏发现进度回滚，需在 Steam 里临时关闭该游戏的云同步。
- **外观只有 guid，没有物品名。** `PlayerOutfit` / `PlayerWardrobe` 存的是资源 guid，外观编辑器做到「guid 级增删 + 原始展示」。挖 guid→物品名映射需要解析游戏资源，成本高，暂未做。
- **`LastDifficulty` 只读。** 这个复合对象带 `_ES3Ref` 引用 ID，改动风险大，界面只展示不允许编辑。
- **游戏大版本更新后可能失效。** 若作者终于换了 Easy Save 3 的默认密码，需改 `Core/SaveCrypto.cs` 的 `Password` 与 `Iterations`；若存档格式变了，重跑 CoreTests 对比字段清单后调整 `Schema/FieldCatalog.cs` 或 `Complex/` 下的解析器。
- 只有 Windows 版（WPF）。

---

## 免责

改的是本地存档，但**可能违反游戏的用户协议**，理论上存在封号风险，自己掂量。联机时等级和金钱会显示在别人界面上。

仅供学习交流。

---

## 许可

**GPL-3.0**。完整正文见 [LICENSE](LICENSE)。拿去改、拿去发都行，改完再发也得开源。

## 致谢与上游

本项目的加密算法逆向、AES 实现逻辑、等级经验公式**源自 [NoirNek0/Phasmophobia-](https://github.com/NoirNek0/Phasmophobia-) 这个纯 Python 实现的前作**（GPL-3.0，代码位于仓库的 [`恐鬼症存档修改器/`](https://github.com/NoirNek0/Phasmophobia-/tree/main/%E6%81%90%E9%AC%BC%E7%97%87%E5%AD%98%E6%A1%A3%E4%BF%AE%E6%94%B9%E5%99%A8) 目录）。上游做了最硬的那部分工作：用 Il2CppDumper 导出 IL2CPP 类型信息、用 capstone 手写反汇编读出 `ES3Internal.AESEncryptionAlgorithm` 的三个参数、并靠读反汇编（而非猜测）定位到「salt 就是 IV」这个反直觉结论。上游 README 还完整记录了四条走过的弯路（游戏方法名被混淆成马拉雅拉姆文、`Experience` 字符串的 588 处引用全是元数据注册桩、浮点常量 `1.73` 其实是角度转弧度表、扫了 44 GB 游戏数据找经验表一无所获），对后来者价值很高。

本仓库在其基础上用 C# 重写了全部实现，扩展为 597 字段全覆盖，并**修正了上游关于等级的过时结论**（见上方「关于等级」一节）。按 GPL-3.0 的传染性要求，本项目同样以 GPL-3.0 发布。

另感谢 [BitterG/Phasmophobia-Save-Editor](https://github.com/BitterG/Phasmophobia-Save-Editor) 与 [stth12/PhasmoEditor](https://github.com/stth12/PhasmoEditor) —— 这两个独立实现的字段处理方式交叉验证了本项目的等级字段结论。

恐鬼症（Phasmophobia）是 Kinetic Games 的作品，本项目与 Kinetic Games 无任何关联。
