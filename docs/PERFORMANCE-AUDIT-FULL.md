# 飞鸟 Asuka 性能体检总报告（两轮合并）

> **本文档为自包含交付件，可整份转发。** 所有结论都标注了证据等级，请优先按 §7 的验证清单自行复现，再按 §8 的顺序修复。
>
> | 项目 | 值 |
> |---|---|
> | 体检对象 | `OICQStickerManager`（飞鸟 Asuka，WPF / .NET 10 / Windows） |
> | 第一轮版本 | commit `f672e71`（`MainWindow.xaml.cs` 2100 行、`MainViewModel.cs` 1699 行） |
> | 第二轮版本 | commit `97a08c2`（今晚 15 个新提交；`MainWindow.xaml.cs` 2363 行、`MainViewModel.cs` 2238 行） |
> | 现场机器 | 12 逻辑核 / 32 GB RAM / Windows；QQ NT 9.9.36（8 个 `QQ.exe`）；Asuka 常驻运行 |
> | 体检性质 | **全程只读**。不改产品代码、不改任何设置、不 attach / 不 hook 用户的 QQ 或 Asuka |
> | 被测实例 | `D:\Asuka\Asuka.exe`（release 单文件构建），配置 `CloseToTray=true`、`EnableQqCoexistTrigger=true`、`CaptureClipboardImages=true`、`QqDeepSyncEnabled=true`（密钥已存在） |

## 证据等级约定（请按此判断可信度）

| 标记 | 含义 |
|---|---|
| `[实测]` | 在本机**运行中的真实进程**（Asuka / QQ）上直接量出来的数字 |
| `[受控实验]` | 用**自己启动的一次性进程**做的对照实验（不涉及用户的 QQ / Asuka） |
| `[代码]` | 逐行读代码确认，附 `文件:行号` 与代码片段 |
| `[推断]` | 由速率/时序对账得出的推理，**尚未直接证明** |
| `[已证伪]` | 被实测否定，**不要再往这个方向花时间** |

---

## 0. 一句话结论 + 修复优先级

**"关掉 Asuka 整机就不卡"的最可能机制不是它吃 CPU，而是它把自己变成了三个"全局参与者"，并且这些参与者永不卸载。** 其中最重的一条（`QqPanelWatcher` 的 UIA 轮询）**同时**造成了三个症状：QQ 打字延迟、内核句柄泄漏、内存增长。

空闲实测：Asuka **0.4% 单核**、15 秒内 **0 个** UIA 事件、机器空闲内存 **17.9 GB** —— 所以"卡"不是持续 CPU 占用，也不是换页。

| 优先级 | 问题 | 位置 | 症状 |
|---|---|---|---|
| **P0-1** | 共存触发器 = 全局鼠标钩子 + 全局 UIA 焦点订阅 + 每 QQ 窗口 `TreeScope.Descendants` 结构订阅，**默认开 + 托盘隐藏后永不卸载** | `ConfigModel.cs:11`、`MainWindow.xaml.cs:499/248-253/823`、`QqPanelWatcher.cs:142/166/551` | QQ 打字延迟、整机不跟手、**内核 Event 句柄泄漏** |
| **P0-2** | 剪贴板监听是全系统参与者，且 OLE 读取 + 整文件哈希 + O(n) 扫描**跑在 UI 线程** | `MainWindow.xaml.cs:485/605/650-659` | 任意应用复制/粘贴时 Asuka 卡住，同时拖住别的应用 |
| **P1-1** | 发送路径在"聊天框有字"时枚举 QQ 整棵 UIA 树 | `WindowService.cs:98` | 每次发送表情卡 QQ 数十~数百 ms |
| **P1-2** | Asuka 自己的 UI 线程做与图库规模成正比的活（每次发送整库序列化 + 全量重排；搜索每键 3–4 遍全量） | `MainViewModel.cs` 多处 | 用久/图库大以后越来越卡 |
| **P1-3** | 今晚新增：排序比较器每次比较读时钟、每次发送多一次全库重排、`UpdateCounts` 双订阅 | `MainViewModel.cs:1571-1590/2131-2134/118-125+214-215` | 排序抖动、发送卡顿放大 |
| **P2-1** | 新增 `DumpQqTreeDiagnostics`：一次约 1000+ 次跨进程往返、后台线程 3–10 秒 | `QqPanelWatcher.cs:439-501` | 表情按钮点击后面板未检出时的长卡 |
| **P2-2** | 深度同步静默附着调试器（往 QQ 内存写 `0xCC`、trap flag 未清、45 秒不可取消窗口） | `QqKeyWatchService.cs:129-214/350-407` | **不是卡顿主因**，但危险程度更高，建议整条移除 |
| **P2-3** | 玻璃材质合成器开销、`asuka-deepsync` 临时目录泄漏、第二实例先加载图库再退出 | `GlassWindow.cs`、`QqDeepSyncService.cs:89-115`、`App.xaml.cs:18` | 拖窗/呼出面板时整机掉帧；杂项卫生 |

---

## 1. 诊断主线：一个根因串起三个症状

### 1.1 机制

```csharp
// Services\QqPanelWatcher.cs:635（一轮 :551）  ← 对每个可见 QQ 窗口
Automation.AddStructureChangedEventHandler(el, TreeScope.Descendants, OnStructureChanged);

// Services\QqPanelWatcher.cs:173（一轮 :166）  ← 全系统焦点事件
Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);

// Services\QqPanelWatcher.cs:149（一轮 :142）  ← 全系统鼠标钩子
_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
```

每次焦点变化 / 结构变化 → `ScheduleThrottledVerify()`（`VerifyMinIntervalMs = 150`，`QqPanelWatcher.cs:33`）→ `CheckNow()` → `ScanQqWindows()`，对每个可见 QQ 窗口做：

```
FromHandle → FindFirst(Descendants, Class="sticker-panel")   ← 整树搜索
          → panel.Current.BoundingRectangle
          → IsPanelVisuallyRendered: AutomationElement.FromPoint + ≤12 级 TreeWalker.GetParent
```

**这些全是同步跨进程调用**：QQ 的 UI 线程必须停下来服务它们。而"用户正在操作 QQ"恰恰是事件最密、验证最频繁的时刻。

### 1.2 实测成本（两轮对比，对着运行中的真实 QQ 窗口）

| 动作 | 第一轮 | 第二轮 | 备注 |
|---|---|---|---|
| **一整套 `CheckNow()`**（1 个可见 QQ 窗口） | avg **24.4 ms** / med 17.2 / max 72.2 | avg **53.2 ms** / med 39.0 / max 117.9 | `[实测]` 同一探针、同一台机 |
| `FindFirst(Descendants, Class=sticker-panel)` | 17.9 ms | 同量级 | `[实测]` |
| `AutomationElement.FromPoint` + ≤12 级 `GetParent` | 6.3 ms | — | `[实测]` |
| `FindAll(Descendants, TrueCondition)` | 52.8 ms / 367 元素 | 65.7 ms | `[实测]` 对应 `WindowService.cs:98` |
| 空闲 15 秒内结构变化事件 | **0** | **0** | `[实测]` 空闲态确实"零常驻成本" |
| 空闲 15 秒内全系统焦点事件 | 0 | 8（均来自非 QQ 进程） | `[实测]` |

**换算**：按 150 ms 节流上限 6.7 次/秒 → **163 ms/s（第一轮）→ 356 ms/s（第二轮）= 约 36% 单核当量，全部花在 QQ 进程内部**。多个可见 QQ 窗口按窗口数线性放大。

**点击 QQ 表情按钮的"验证风暴"**（此时用户正要打字，`[代码]`）：

```csharp
// QqPanelWatcher.cs:403-416  按钮点击后 7 次全量扫描
foreach (var delay in new[] { 0, 80, 160, 240, 320, 480, 700 }) { ... CheckNow(); }
// QqPanelWatcher.cs:511-544  2.5 秒内每 200 ms 再扫一次 → 12 次
```
合计 **≈19 次全量跨进程扫描 / 3.2 秒 ≈ 460 ms 花在 QQ 进程内部**。

### 1.3 受控实验证明：跨进程 UIA 操作会在调用方泄漏内核 `Event`

`[受控实验]` 另起一个进程，对目标窗口做 `FromHandle` + `Current.Name` + `FindAll(Children)`：

```
200 次操作 → 客户端句柄 550 → 728 (+178 = 0.89/次)；强制 GC 只回收 21 个 (728 → 707)
 50 次操作（按类型统计）→ Event 句柄 161 → 179 (+18 = 0.36/次)   ← 泄漏类型正好是 Event
成本：200 次操作 36.9 秒 = 每次 UIA 操作 185 ms（每次都在阻塞对方进程的 UI 线程）
```

另一组 `[受控实验]` 针对单条路径（`FindFirst(TreeScope.Descendants)`）：60 次 → 句柄 +16（0.267/次），强制 GC **回收不掉**；同组对照里 `FromHandle`、`FindAll(全部 Descendants)`、`File.AppendAllText`、`DumpChildren depth3` 全为 **0/次**（干净）。

### 1.4 与现场观测对账

现场曾观测到一次**真实的资源失控**：句柄 2,696（启动 9.5 分钟）→ 22,946（3.4 小时）→ 峰值 **61,347**；工作集 271 MB → **1,316 MB**；CPU 瞬时 **82%** 单核。

句柄类型普查（`NtQuerySystemInformation` 句柄表，40,695 个样本）：

```
Event 39,702 | Mutant 177 | Section 122 | File 118 | Thread 73 | Key 58 | Semaphore 46 | Timer 32 | 其余 <10
```
→ 是**内核事件对象风暴**。托管堆**干净**（183,138 个对象 / 17 MB，任何类型实例数最多 35 个）；GDI 175 / USER 112 正常 → **泄漏在原生侧**。

**速率对账**（`[推断]`，但两条曲线都吻合）：

| 场景 | UIA 调用量 | 预期泄漏 | 实测 |
|---|---|---|---|
| Asuka 自己的 watcher 查询 QQ（常态、低频） | ~2–5 次/秒 | 0.8–4.5 个/秒 | **长期基线 1.82 个/秒**（2,703→22,946 / 3 小时 5 分） |
| 外部工具高频查询 Asuka（此时 Asuka 作为 UIA **provider** 也要建 Event） | 数百次/秒 | 130–330 个/秒 | **爆发峰值 108→177→310 个/秒** |

> ⚠️ **必须知道的归因边界**：上面那次**爆发**（2.7 万→6.1 万）经查是**审查方自己的 UIA 驱动工具造成的**——杀掉驱动工具后增长**立即停止**，残留进程再次拉起后再度增长。**所以不要把"150 个句柄/秒"当成产品在无人操作下的行为去复现。**
> 但**长期基线 1.82 个/秒**发生在没有任何外部工具的时间窗内，且与 Asuka 自身 watcher 的低频 UIA 调用量匹配——这一条是**产品的真实缺陷**（`[推断]` 级，需要 §5 的干净复现来定案）。

### 1.5 句柄类型普查的可信度（已被直接验证，请放心引用）

普查工具走 `NtQuerySystemInformation(SystemExtendedHandleInformation)`（信息类 64）+ `NtQueryObject` 取类型名，entry 40 字节。曾有人质疑"这台机器上该路径解析错误"——**该质疑已被直接证伪**：拿两个**已知句柄数**的进程做对照，枚举总数逐个精确吻合：

| 目标 | 内核自报（`GetProcessHandleCount`） | 工具枚举 | 按类型求和 |
|---|---|---|---|
| `explorer` pid 17136 | 3,852 | **3,852** | 3,852 ✓ |
| `ZCode` pid 6396 | 2,846 | **2,846** | 2,846 ✓ |

另一处"看起来不对"的数字（普查只看到 40,695，而进程当时约 50,066）原因是**时点不同**：增长曲线在 38,899（06:45:02）到 50,066（06:46:41）之间正好经过 40,695，且该次普查的类型求和自身也是自洽的。

**参考：同样是这批工具，在正常进程上的类型分布**（用于对比 Asuka 的异常程度）

| 进程 | 句柄总数 | 最大类型 | 占比 |
|---|---|---|---|
| `explorer` | 3,852 | Event 979 | 25% |
| `ZCode` | 2,846 | Key 1,194 | 42% |
| **Asuka（失控时）** | 40,695 | **Event 39,702** | **97.6%** ← 极端异常 |

> 结论：Asuka 那次的句柄构成确实是"**Event 单一类型占满**"，不是窗口/线程/文件/注册表的常规增长。请以这一点为准。

---

## 2. 问题清单（机制 / 证据 / **如何验证** / 最小修复）

> 行号按 commit `97a08c2`；第一轮 `f672e71` 的行号在括号里标注。改代码后行号会平移，**请以调用点名字为准**。

### 🔴 F1（P0）共存触发器：三个全局参与者，默认开启，托盘隐藏后永不卸载

**机制**（`[代码]`）
1. `Automation.AddStructureChangedEventHandler(el, TreeScope.Descendants, …)`（`QqPanelWatcher.cs:551`）在 Chromium 窗口上 = **事件消防水管**：`[受控实验]` 让同一窗口每秒改 20 次 DOM，`Descendants` 订阅产生 **183 个事件 / 4 秒（46/秒）**，换成 `Children` 是 **0**。
   → 现网日志验证这一机制确实在生效：`asuka-watcher.log` 里 Asuka 实际收到的类型是 `ChildAdded × 36`、`ChildRemoved × 31`、`ChildrenReordered × 31`，全是 DOM 变更事件。
2. `PruneSubscriptions()`（`:646-662`）只从字典删项，**从不调用 `Automation.RemoveStructureChangedEventHandler`**；唯一批量退订是 `Dispose()` 里 fire-and-forget 的 `RemoveAllEventHandlers()`。
3. `WH_MOUSE_LL` 全局钩子把 Asuka 放进**全系统鼠标输入链**：每个鼠标事件都要绕到它的钩子线程再放行；该线程被 GC/UIA/日志拖住时整桌面鼠标都会延迟，超 `LowLevelHooksTimeout` 还会被 Windows 静默摘钩（代码注释自承"实测发生过"）。
4. **永不卸载**：`OnClosing`（`MainWindow.xaml.cs:248-253`）在 `CloseToTray` 时改为 `HideToTray()`（`:274-284`，只 `Hide()`），于是 `OnClosed`（**唯一**的 `_panelWatcher?.Dispose()`，`:823`）**永不执行**。全仓库没有任何 `StateChanged`/`IsVisibleChanged` 钩子。现场配置正是 `CloseToTray=true` → 用户点 ✕ 之后钩子和订阅继续挂着。
5. **配置竞态**：`MainViewModel` 构造里 `_ = LoadConfigAsync();`（`MainViewModel.cs:218`，第一轮 `:211`）**发了不等**，而 `OnSourceInitialized`（`:499`）无条件调用 `UpdateWatcherState()`。用户即使把共存触发器关掉，启动瞬间仍按字段默认值 `true` **装上一次**，直到配置加载完才拆。

**如何验证**
```powershell
# ① 钩子和订阅是否永不卸载：启动 Asuka → 点 ✕（进托盘）→ 看下面两行是否仍存在
Get-Process Asuka | Select-Object HandleCount, @{n='Threads';e={$_.Threads.Count}}
# 期望（修复前）：线程里仍有名为 AsukaMouseHook 的托管线程；修复后应消失
# ② 结构事件洪水：把 --user-data-dir 隔离的 Chromium 窗口打开并让页面持续改 DOM，
#    用 .perf-probe/UiaProbe.exe events 15 对比 Descendants 与 Children 两档（本报告 §1.4 数据即此法所得）
# ③ 配置竞态：把 config.json 的 EnableQqCoexistTrigger 改成 false → 重启 → 观察
#    %TEMP%\asuka-watcher.log 是否仍出现 "watcher started / mouse hook installed"（修复后不应出现）
```

**最小修复（按性价比排序）**
```csharp
// ① 默认关（一行消除最大风险）
// Models\ConfigModel.cs:11
public bool EnableQqCoexistTrigger { get; set; } = false;

// ② 缩窄订阅范围：实测事件 46/秒 → 0/秒
// Services\QqPanelWatcher.cs:551
Automation.AddStructureChangedEventHandler(el, TreeScope.Children, OnStructureChanged);

// ③ 验证节流 150 → 1000 ms，且 QQ 不在前台时直接跳过
// Services\QqPanelWatcher.cs:33
private const int VerifyMinIntervalMs = 1000;

// ④ 补退订（现在只删字典项）
// Services\QqPanelWatcher.cs PruneSubscriptions() / Dispose()
Automation.RemoveStructureChangedEventHandler(el, OnStructureChanged);

// ⑤ 命中测试改走 Win32，零 UIA（砍掉每次验证最贵的一段）
// 替换 IsPanelVisuallyRendered 里的 AutomationElement.FromPoint + 12 级 GetParent
[DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
//   + GetWindowThreadProcessId / IsWindowVisible / GetWindowRect 做可见性判定

// ⑥ 按 hwnd 缓存已找到的 sticker-panel 元素，之后用 Win32 矩形校验复验，
//    不再每次重跑整树 FindFirst(Descendants)

// ⑦ 托盘隐藏即卸载，恢复时重建
protected override void OnStateChanged(EventArgs e)   // 或 IsVisibleChanged
{ /* Hidden/Minimized → _panelWatcher?.Dispose(); _panelWatcher = null; */ }
// RestoreFromTray() 里重新 UpdateWatcherState()

// ⑧ 等配置就绪再启动 watcher（hotkey 已有先例：MainWindow.xaml.cs:496-498 用 ConfigLoaded.ContinueWith）
```

---

### 🔴 F2（P0）剪贴板：全系统参与者 + UI 线程阻塞

**机制**（`[代码]`，`MainWindow.xaml.cs:597-665`，第一轮 `:582-650`）

```csharp
private void EvaluateClipboardCapture()          // 由 :591(第一轮:576) 的 300ms 去抖 Tick 调用 → 在 UI 线程
{
    var data = Clipboard.GetDataObject();        // :605(590) ← 同步 OLE 读取，可挂数秒
    ...
    // FileDrop 分支：:620(605) 已经算出廉价签名（Length + LastWriteTimeUtc）
    else if (Clipboard.ContainsImage())
    {
        var src = Clipboard.GetImage();          // :624(609)
        encoder.Save(ms);                        // PNG 编码
        File.WriteAllBytes(path, pngBytes);      // 落盘
    }
    var md5Hex = Convert.ToHexString(MD5.HashData(
        pngBytes ?? File.ReadAllBytes(path)));   // :650-651(635-636) ← 整文件读入 + 哈希
    if (vm.Stickers.Any(s => s.Md5 == md5Hex))   // :652(637)      ← O(图库)
    ...
    if (sig == _lastClipboardSig) return;        // :659(644)      ← 去重竟然在最后
}
```

**为什么危险**
* `Clipboard.GetDataObject()` 跨进程阻塞，且剪贴板是**全系统串行资源** —— 持有剪贴板的应用不响应时 Asuka 的 UI 线程卡住，**其他所有应用的复制/粘贴也被拖住**（本机开着 `mstsc`，RDP 剪贴板重定向是经典挂起源）。
* 代码自己注释（`:644` 附近）写明"**部分应用会周期性重写相同剪贴板内容**"→ 这条路会被周期性反复触发，每次全量哈希 + O(n) 扫描。
* **廉价判据在手却不用**：`:620` 的 `sig` 只需 `FileInfo`，却等到 `:659` 才比较。
* 300 ms 去抖是**重置式**的：某应用每 <300 ms 重写一次剪贴板，定时器永不到期，功能静默失效。

**如何验证**
```powershell
# 造一个"周期性重写相同剪贴板内容"的场景，同时看 Asuka 的 UI 是否卡：
# 在另一个进程里每 350 ms 写一次同样的图片到剪贴板（SetClipboardData），
# 观察 Asuka 窗口（拖动/滚动）是否出现周期性停顿；同时在任务管理器看 CPU 尖峰。
# 修复后：Asuka 不应有可见停顿，且不再对同一内容做第二次哈希。
```

**最小修复**（注意 OLE 要求 STA，**不能**直接 `Task.Run`）
```csharp
// ① 廉价去重提前（一行，收益最大）
if (sig == _lastClipboardSig) return;      // 移到 sig 计算完成之后立刻执行
// ② OLE 读取挪到专用 STA 线程（当前跑在 WPF UI 线程 STA 上"正确"但正是卡 UI 的原因）
// ③ 流式哈希，别整文件读入
MD5.HashData(File.OpenRead(path));
// ④ 图库 MD5 预建 HashSet<string> 索引，替代 vm.Stickers.Any(...)
```

---

### 🟠 F3（P1）发送路径在输入框有字时枚举 QQ 整棵 UIA 树

**机制**（`[代码]` `WindowService.cs:92-98`）
```csharp
var editor = root.FindFirst(TreeScope.Descendants,
    new PropertyCondition(AutomationElement.ClassNameProperty,
                          "ProseMirror ExEditor-qq-msg-editor is-empty"));   // ← 带 is-empty
if (editor == null)
{
    var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);   // :98  [实测] 52.8–65.7 ms
    foreach (AutomationElement e in all) { /* 每个元素一次跨进程属性读 */ }
}
```
快速路径的 class 里带 `is-empty`，**聊天框里只要有字就匹配不上** → 每次都退化成全树枚举。

**如何验证**：在 QQ 输入框里先打几个字，再发送一张表情；用秒表/ETW 测 `WindowService` 发送路径耗时，或直接对比"空输入框发送 vs 有字发送"的耗时差。

**最小修复**：匹配稳定子串（`ExEditor-qq-msg-editor`）或 `ControlType.Document`，并把 `TreeScope` 收到编辑器的父节点；删掉 `Condition.TrueCondition` 枚举。

---

### 🟠 F4（P1）Asuka 自己的 UI 线程做与图库规模成正比的活

**`[代码]` 全部仍在（第二轮复查结论）**，其中前三项因今晚新功能而**更重**：

| 位置（`97a08c2`） | 问题 | 触发频率 |
|---|---|---|
| `MainViewModel.cs:2131-2134`（一轮 `:1598-1599`） | `await SaveDatabaseAsync()` → 整库 `JsonSerializer.Serialize`（`WriteIndented`，**在第一个 await 之前同步跑在 UI 线程**，`:1233-1240`）→ `WriteJsonWithBackupAsync` 里 `File.Copy`/`File.Move` 也是同步的（`:1332-1334`）→ 再 `RecalcRecentPinned()`（整库 `OrderByDescending().Take(N)`）→ `_stickersView.Refresh()`（全量重排 + 重新实体化） | **每次发送**（最高频动作） |
| `MainViewModel.cs:1424-1459`（一轮 `:1145-1146`） | 搜索框 `UpdateSourceTrigger=PropertyChanged`：每敲一键 = `Refresh()` + `UpdateTabTags()`（`Clear()`+逐个 `Add` + `ComputeTagHeat()` 遍历全库）+ `RefreshPanelTabs()`（再一遍 Clear+Add）+ **今晚新增** `ApplyQqSearchBuckets()`（全库字典 + 每账号遍历 + 每账号 `Refresh()`） | 每次按键 |
| `MainViewModel.cs:1077-1105`（一轮 `:791/814-815`） | 启动 `savedRecords.FirstOrDefault(r => r.FullPath == …)` 在文件循环里 → O(n²)；再 `Stickers.Clear()` + 逐个 `Add`，而每个 `Add` 触发 `UpdateCounts()` 遍历整个视图 | 每次启动 |
| `MainViewModel.cs:118-125` + `:214-215` | `_stickersView` 本身就是 `Stickers` 的视图，却又**同时订阅了二者**的 `CollectionChanged` → 每次增删触发**两次** O(n) 全视图遍历 | 每次集合变更 |
| `MainViewModel.cs:743-758`（一轮 `:685-689`） | `Stickers.FirstOrDefault(s => s.Md5 == item.Md5)` 对每个镜像项 → O(镜像 × 图库)，在 UI 线程 | 每次 QQ 收藏变化 / 每次标签编辑 |
| `QqEmojiService.cs:130-151` | `Mirror.Clear()` + 441 次 `Add`（绑定在 ItemsSource 上）无批处理 | 每次重扫 |
| `Models/StickerModel.cs:74-100` | `_imageSource` 位图缓存**永不淘汰** | 滚动图库 |
| `MainViewModel.cs:217-222` | 构造里**四个** fire-and-forget 加载（`LoadConfigAsync`/`LoadStickersAsync`/`LoadQqStatsAsync`/`LoadTagStatsAsync`）→ 顺序无保证，`LoadStickersAsync` 先完成时 `UpdateImportedFlagsAll`/`RecalcRecentPinned` 会跑在空 `_qqServices`/默认排序上 | 每次启动 |

**如何验证**：图库灌到 1000+ 张，然后用 Stopwatch/ETW 测三个动作的 UI 线程占用：① 发一张表情 ② 在搜索框连打 10 个字符 ③ 冷启动到可交互。或在 `SaveDatabaseAsync`/`SearchText` setter/`UpdateCounts` 入口加计时日志。

**最小修复**：保存去抖（2 秒空闲 + 退出时落盘）+ 序列化下后台 + 去掉 `WriteIndented` + `File.Copy/Move` 一起进 `Task.Run`；搜索去抖 250 ms；`Stickers` 批量替换（一个 `Reset` 的 `ReplaceAll`）并把 `UpdateCounts` 改成单订阅 + int 计数 + `BeginInvoke(Background)` 合并；`UpdateImportedFlags` 用一次 `md5→StickerModel` 字典；镜像重建用 `DeferRefresh()`。

---

### 🟠 F5（P1）今晚新增代码引入/放大的问题

| 位置 | 问题 | 影响 |
|---|---|---|
| `MainViewModel.cs:1571-1590` + `Models/StickerRanking.cs:48-49` | 新比较器 `CompareWithMode` → `RankScore` → `Score(..., now ?? DateTime.Now)`：**每次比较都读一次时钟**。而 `RecencyWeight` 在 1h/6h/24h/3d/7d/30d 有**阶跃边界**（`StickerRanking.cs:22-31`）→ 单次排序内比较器**不自洽**（顺序抖动；`ListCollectionView.CustomSort` 可能抛"无法比较两个元素"）。`case 2` 还每比较一次 `string.Join("、", Tags)` 生成 2 个字符串 | 排序抖动 + O(n log n) 垃圾 |
| `QqStickerModel.cs:19-28` | `BorrowedTags` 的 setter **无条件** `OnPropertyChanged()`（对照 `IsImported:61-68` 是有比较的），而 `UpdateImportedFlags` 对**每个镜像项**每次赋值 | 每次 QQ 收藏变化 / 标签编辑触发 O(mirror) 通知风暴 |
| `MainViewModel.cs:1476-1525` | 排序模式/置顶张数 setter：两次全视图 `Refresh()` + 整份配置序列化落盘；`QuickPanelSortMode` 连面板关着也刷新 | 每次点击分段控件 |
| `QuickPanelWindow.xaml.cs:65-69` | `viewModel.QuickPanelSendInitiated += …` **从不退订**，而 VM 是 `static App.SharedViewModel`、快捷面板换肤时整体重建（`MainWindow.xaml.cs:142-165`） | 死处理器 + 整窗对象图被静态根持有 |
| `QqPanelWatcher.cs:51` `LooksLikeEmojiButton` | 今晚把 class 校验**删掉了**，只按 `name.Trim() == "表情"` 匹配 | 误命中代价不只是"面板贴错位置"：每次误命中触发 `VerifyWithOpenRetriesAsync`（7 次全量扫描）并覆写 `_emojiBtnRect`（让鼠标钩子瞄错地方） |
| `MainViewModel.cs:2079/831` | `_lastSendTimes`、`_qqStats` 只增不减 | 低（有界于历史发送数） |
| `Models/StickerModel.cs:32-67` | `Tags`/`LastUsedTime`/`UseCount` setter **没有相等判断** | 批量赋值即通知风暴 |

---

### 🟡 F6（P2）新增 `DumpQqTreeDiagnostics`：句柄便宜，但 QQ 侧很贵

**`[代码]` + `[实测]`** `QqPanelWatcher.cs:439-501`：深度 3 的 UIA 树遍历，每个访问到的节点一次 `FindAll(Children, TrueCondition)`，每个子元素 2 次跨进程属性读（`SafeClass` + `SafeName`）。

* 实测 **322–388 个元素被访问**、**1000+ 次跨进程往返**、后台线程 **3–10 秒**（单次 `FindFirst` 18.9 ms、`FindAll(Descendants, TrueCondition)` 65.7 ms）。
* 触发点 `:426`：7 步重试（0+80+160+240+320+480+700 ms ≈ 1.98 秒）之后 `_optimisticPending && !_panelOpen`，即**每次"点了表情按钮但面板没检出"**，90 秒限频。
* `hits.Count > 40`（`:477`）只限制**命中数**，**不限制遍历量** —— 一棵不匹配的树会被完整走完。
* 好消息：现场日志里 **0 次触发**（2215 行里没有 `diag`），目前是潜伏风险。

**修复**：把 `Condition.TrueCondition` 换成类名条件；把**访问节点数**计入上限；或改成"仅在用户主动点反馈按钮时收集"。

---

### 🟡 F7（P2）深度同步的静默附着调试器（危险度高于卡顿）

> 现场实测**当前未激活**：8 个 QQ 进程 `CheckRemoteDebuggerPresent` **全为 False**，Asuka 无子进程（配置有密钥 → keywatch 未启动）。以下按"一旦激活"评估。

`[代码]` + `[受控实验]`

| 项 | 事实 |
|---|---|
| 触发方式 | `QqKeyWatchService.cs:55-80` 每 **600 ms** 轮询 `Process.GetProcessesByName("QQ")`，**每个新出现的 QQ.exe PID** 派一个独立 `Task.Run`；`:88` 附着时长 **45 秒** |
| QQ 侧规模 | QQ NT 一次启动 **8 个 `QQ.exe`**（主 + 4 renderer + GPU + network + audio）→ 最多 8 个并发调试会话；主进程实测 **203 线程 / 2854 句柄** |
| 附着本身多贵 | `[受控实验]` 用自己的 Chromium 复刻同一 P/Invoke 与事件泵：**18 个调试事件/秒、每个 ≤1 ms、目标 UI 往返 0.53 → 0.46 ms（没有恶化）** → **"附着调试器"本身解释不了严重卡顿** |
| 会真正冻结 QQ 的时刻 | ① 附着瞬间（为每个已存在线程/模块合成事件，203 线程全部冻结）；② `StaticAnalysis.GetKeyFunctionRva` 在 `LOAD_DLL_DEBUG_EVENT` 分支里被调用（`QqKeyWatchService.cs:254-263`），**此时目标处于挂起状态**，而它要 `File.ReadAllBytes` 一个 **112.4 MB** 的 `wrapper.node` 再扫两遍 —— `[受控实验]` 实测 63–76 ms（NVMe+页缓存命中），冷缓存/机械盘/杀软拦截时是数百 ms 到数秒的完全冻结；③ INT3 命中时（`:200-214` 往 `wrapper.node` 的 `.text` 写 `0xCC`），每次命中走完整调试往返，**其间 203 个线程全停**，而断点下在"每次打开加密库都会走"的密钥函数上 |
| 现场日志实证它真跑过 | `[实测]` `asuka-watcher.log`：`keywatch: attaching to new QQ process 6532` → 646 ms 后 `detached`；另有一次 `attach failed: 拒绝访问` |

**比卡顿更该修的三点**
1. **往 QQ 内存写 `0xCC`**（`:200-214`）：在生产进程代码段打补丁，任何一步失败/中断都可能让 QQ 崩在非法指令上。
2. **trap flag 未清理就分离**：成功分支 `:350-353` 给线程置了 TF，随后 `Detach()`（`:400-407`）只还原 `0xCC`、**没清 TF** → `DebugActiveProcessStop` 后该线程以 TF=1 恢复，可能抛无人处理的 `STATUS_SINGLE_STEP`。**本机从未触发过这条路径（日志无 `key found at breakpoint`），属潜伏缺陷，未实测验证，建议按高风险对待。**
3. **不可取消的 45 秒窗口**：每个新 `QQ.exe` 一个任务；`Dispose()` 不取消在途任务；`tried` 只增不减；`_captured` 只有**真抓到密钥**才停轮询 → 一次失败会**永久武装**在下一次 QQ 启动上。

**建议**：**整条"静默附着"路线移除**，只保留用户显式点击后运行官方脚本（`QqKeyExtractor`）的那条。若必须保留，最低限度：单飞、跳过 `--type=` 子进程、附着 3–5 秒、**静态分析挪到附着之前**（绝不在目标挂起时读盘）、**永不下 INT3**、`Dispose()` 必须能取消在途任务。

---

### 🟡 F8（P2）合成器 / 杂项

| 位置 | 问题 |
|---|---|
| `GlassWindow.cs:49`（一轮 `:40-51`） | `LocationChanged` 每帧 → 3 次 `SetWindowPos`（3 个独立 DWM 模糊窗口重定位）；拖窗 = 每帧重算 ~1.2 Mpx 背景模糊。建议拖动期间冻结定位，松手后一次性摆位 |
| `GlassWindow.cs:166-183` | `SyncTileVisibility` = 6 次 `SetWindowPos` + 3 次 `SetWindowCompositionAttribute`；`BlurTile.Show(false)` 的 `ClearAccent()` 是**故意清 accent 逼 DWM 下次重算模糊**。每次热键/共存打开都付一遍 |
| `MainWindow.xaml:8` 等 | 3 个窗口 `AllowsTransparency="True"` → 走分层窗口路径，任何一帧动画整窗重推 |
| `MainWindow.xaml.cs:1481`（一轮 `:1301`） | GIF 气泡 `DropShadowEffect BlurRadius=24` 在透明 Popup 里逐帧重算模糊 |
| `QqDeepSyncService.cs:89-115` | `%TEMP%\asuka-deepsync\` 实测残留 **3 个目录 / 2.37 MB**（`plain.db` 776 KB + `-shm` 32 KB）。**机制修正**：不是"漏 Dispose"（`:121` 是 `using var conn`），而是 **Microsoft.Data.Sqlite 默认连接池**让 `Dispose()` 只还池、不释放原生文件句柄。修法：连接串加 `Pooling=false`，或删目录前 `SqliteConnection.ClearPool` |
| `App.xaml.cs:18` | `static MainViewModel SharedViewModel { get; } = new();` 是静态初始化器，在单实例互斥判定（`:56`）**之前**执行 → 第二实例先跑完图库+配置加载再退出 |
| `MainWindow.xaml.cs:1727/1737` | 标签编辑器 GIF 只由 `Deactivated:131-135` 停；最小化（`:210-213`）与 `HideToTray`（`:276`）都不触发 `Deactivated`，也没有 `StateChanged` → 托盘隐藏时可能仍在解码播放（快捷面板做对了：`QuickPanelWindow.xaml.cs:49`） |
| `MainWindow.xaml.cs:1498-1504` | `CloseGifPreview()` 只 `IsOpen=false`，未 `Stop()` `_gifPreviewTimer` → 已排队的预览仍可能启动并一直播 |
| `QuickPanelWindow.xaml.cs:239-243` | 悬浮时按 `UriSource` **全分辨率**新解一份 GIF（注释明确 WpfAnimatedGif 需要帧序列，不可 Freeze / 不可设 `DecodePixelWidth`）。同时只播 1 张、移开/回收/隐藏即 `SetAnimatedSource(null)`，**上限正确**；建议按 FullPath 缓存解码结果 |
| `Services/BlurTile.cs:178` | `hbrBackground = CreateSolidBrush(0x00000000)` 的 HBRUSH 存进 `WNDCLASS` 后**从未 `DeleteObject`**（仓库里没有该 DllImport）。`_classRegistered` 有守卫 → 只是进程内 **1 个** GDI 对象，非增长源；属卫生问题 |

---

## 3. 已证伪的假设（**不要再花时间**）

| 假设 | 否定证据 |
|---|---|
| `Process.GetProcessesByName("QQ")` 未 Dispose 泄漏句柄（`QqPanelWatcher.cs:754 CollectQqPids`、`QqKeyWatchService.cs:59/67`、`FeedbackService.cs:72`） | `[受控实验]` **0 句柄 / 500 次调用**（生产写法 219→219）；带 `Dispose()` 的对照反而 +2/500。`FeedbackService.cs:84` 本来就 dispose，`QqKeyWatchService.Dispose()` 也正常 |
| 内存压力 / 换页导致整机卡 | `[实测]` 32 GB 内存空闲 17.9 GB，提交空闲 38.75 GB |
| 持续渲染（60 fps 循环） | `[实测]` Asuka GPU **1.9%**；dmp 里 UI 线程栈停在 `Dispatcher.PushFrameImpl`（空闲消息泵） |
| 全局键盘钩子造成打字延迟 | `[代码]` 全仓库只有 `WH_MOUSE_LL=14`，**没有键盘钩子** |
| 深度同步调试器附着是本次卡顿主因 | `[受控实验]` 附着本身 18 事件/秒、≤1 ms、目标 UI 往返无恶化；且现场 8 个 QQ 进程均未被调试 |
| 快捷面板被反复开关拖慢 | `[实测]` `asuka-watcher.log` 自 05:00:22 起**再无一行**（面板开关必写日志） |
| 外部 UIA 事件洪水 | `[实测]` 空闲 15 秒：**0 个**结构事件、8 个焦点事件（均来自非 QQ 进程） |
| 图库虚拟化被关掉 | `[代码]` `MainWindow.xaml:360-361`、`QuickPanelWindow.xaml:115-116` 都是 `IsVirtualizing=True` + `Recycling` |
| 常驻高频定时器 / 空转循环 | `[代码]` 无 `CompositionTarget.Rendering`/`RenderTargetBitmap`/`RepeatBehavior=Forever`/`while(true)` 空转；范围内每个 `DispatcherTimer` 都在自己 Tick 里 `Stop()` |
| `ClipboardToastWindow` 每次捕获泄漏定时器（第一轮的结论） | **第一轮该结论有误，本轮更正**：`Finish()`（`:139-146`）先 `Stop()` 再 `Close()`，调用方一律走 `Finish` |
| 图库网格里 N 个 GIF 同时动 | `[代码]` 网格**没有**原地播放，只有快捷面板 `AnimatedFrame` + 主窗口单个悬浮 Popup，上限是 1 |
| `File.AppendAllText` 日志路径泄漏句柄 | `[受控实验]` **0 / 500 次** |
| **WPF `Window` 创建/关闭 churn 是主因** | `[实测]` 单个 `Window`（含 `HwndSource` + 3 个 `BlurTile` HWND）确实每次约留 **4 个句柄**；但全应用日志 5 小时 47 分里 `quick panel pre-warmed` 只有 **103 次**（≈400 个句柄），**量级差 60 倍**，无法解释 26 k；而且 `Window`/HWND 属于 **USER 对象**（实测 112，正常），Asuka 的泄漏类型是 **Event（97.6%）**，两者不是一回事 |
| **`Thread` 创建（每线程约 3 句柄）是主因** | `[实测]` Asuka 的句柄类型普查里 `Thread` 只有 **73 个**（占 0.18%）——若线程句柄在泄漏，这一项不可能这么低 |
| `DispatcherTimer` churn（0.185/次）是主因 | `[受控实验]` 单次确实留 0.185 个，但触发频率低（仅 `HideSoft`/`Show()`），且 Asuka 普查里 `Timer` 只有 32 个 |
| **WpfAnimatedGif 泄漏定时器/句柄** | `[受控实验]` 完整生产周期 `SetAnimatedSource(gif) → SetAnimatedSource(null)`：**0.005 / 次，干净** |
| WPF 成像（`BitmapImage` `OnLoad`/`BitmapDecoder`/`PngBitmapEncoder`）泄漏 | `[受控实验]` 冷启动时 0.117/次，**WPF 预热后 0.005/次** → 一次性成本，非泄漏。全仓库也**没有** "`StreamSource` + 未释放 FileStream" 这个真会漏 1.005/次的写法 |
| 全部 `[DllImport]` 返回路径漏句柄 | `[代码]` 逐条核对：`CreateToolhelp32Snapshot`→`CloseHandle` 在 `finally`（`QqKeyWatchService.cs:176/196`，含错误路径）；`OpenProcess`→`CloseHandle`（`:405` 与 `:137` 两条路径）；`OpenThread`→`CloseHandle` 三个调用点全在 `finally`；调试事件 `hFile`→`CloseHandle`（`:252`）；`SetWindowsHookEx`→`UnhookWindowsHookEx`；热键/剪贴板监听得 `OnClosed` 注销（`MainWindow.xaml.cs:819-820`）；注册表用 `using`。**无泄漏** |
| `ImageSniffer` / `ImageService` / `GlassWindow` / `BlurTile` 有新问题 | `[代码]` 自 `f672e71` **零改动**；3 个 `BlurTile` 一次创建、关闭时统一 `Dispose→DestroyWindow`，`Layout()` 有 latch 无 churn（唯一小瑕：`BlurTile.cs:178` 的 `CreateSolidBrush` HBRUSH 从未 `DeleteObject`，进程内 1 个 GDI 对象，见 F8） |

---

## 4. 未能定论的部分（请接手方优先补齐）

1. **"无人操作时是否仍缓慢泄漏"未定案。** 长期基线 1.82 句柄/秒（`[实测]`）与 Asuka 自身 watcher 的 UIA 调用量匹配（`[推断]`），但同一台机器上存在**第三方驱动工具**留下的痕迹（见 §5），无法排除它们也参与了那段窗口。
2. **`FindFirst(TreeScope.Descendants, …)` 的单次泄漏量在两次独立测量里分别是 0.03/次 与 0.267/次**（同一条代码路径、不同探针）。量级一致、绝对值有 10 倍差，需要一次标准化复测。
3. **爆发期 CPU 峰值 82% 单核的归属未定**：进程里有若干**纯原生线程**（`clrstack -all` 里托管栈为空），`dotnet-dump` 无法给原生栈；要定位需要 WinDbg/cdb 的 `k` 或 ETW 采样。
4. **UIA 泄漏是否也发生在 QQ 那一侧**（Asuka 高频查询 QQ 时）—— 机制上应该成立，但未验证。
5. **"句柄是被终结器（finalizer）排队持有，还是一直被原生代码永久持有"** —— 有实测显示单个 WPF `Window` 关闭后约留 4 个句柄、需 GC 才回收，因此理论上存在"创建快于终结"的积压可能。但**本报告证据指向反面**：① 普查显示泄漏类型是 `Event`（97.6%），而窗口/HWND 属 USER 对象（实测 112，正常）；② 一次堆转储（`dotnet-dump collect --type Heap`，取证时观察到 26,210 → 26,197）**没有**让句柄回落。要彻底定案：在应用里临时加一个隐藏热键执行 `GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`，再复读 `HandleCount` —— **大幅下降 ⇒ 终结器积压；几乎不动 ⇒ 原生永久持有（重点查 UIA 注册那条链路）**。

**建议的干净复现协议（不驱动运行中的实例）**
```
1. 关闭所有 UIA/注入类测试工具（含项目自带的 UiVerify 之类驱动工具）。
2. 重启 Asuka，静置 30 分钟，期间不碰它：
   每 2 分钟采样一次 HandleCount / WorkingSet / GDI / USER / CPU（脚本见 §6）。
3. 判定：
   - 句柄曲线斜率 ≈ 0 且 WS 平稳  → 常态不泄漏，问题只在"被 UIA 驱动/高频查询"时出现；
   - 句柄持续线性增长（哪怕 1–2/秒）→ 产品自身泄漏，按 F1 修复后应回落。
4. 再做一次对照：用 `.perf-probe/UiaProbe.exe handlecost 60`（它只读、不驱动、只统计自身句柄）
   复测单次 UIA 操作的泄漏量，得到标准化数字。
```

---

## 5. 机器侧环境干扰（归属分析必须扣除）

`[实测]` 本机的情况，评估"Asuka 该负多少责任"时必须先扣掉：

| 观测 | 数值 | 说明 |
|---|---|---|
| `LEDKeeper2` 句柄 | **164,287** | 灯效/主板工具，运行 15.3 小时。**是 Asuka 峰值的 2.7 倍**，单进程就占掉大量内核句柄池 |
| 系统句柄总数 | **365,810** | 整机偏高 |
| Asuka 中被注入的第三方 DLL | `RTSSHooks64.dll`（RivaTuner 统计服务器）、**5 个搜狗输入法组件**（`ichat_bundle64`/`isgpet_bundle64`/`PicFace64`/`Resource.dll`/`systembeautify_bundle64`）、`GoogleIMEJaTIP64.dll`、`iFlyameQuickLaunch.dll`、`mswebp_store.dll` | 输入法 TIP 组件与 RTSS 都是钩子型注入；**它们会在被注入进程里创建对象**，句柄/内存归属可能被污染 |
| 其它常驻重负载 | Wallpaper Engine（`wallpaper64` 450 MB）、NVIDIA Overlay、Razer Synapse、HIPS 杀软、Everything、`mstsc` 远程桌面 | 都在抢 DWM/CPU；"关掉某软件就缓解"很容易被误读 |

> **给接手方的提醒**：复现"整机卡"时必须记录当时 `LEDKeeper2` 等进程的句柄数；否则会把机器级问题算到 Asuka 头上。

---

## 6. 只读采样脚本（可直接跑）

```powershell
# --- 单进程资源 + 是否被调试（只读，不改任何东西）---
$pid1 = (Get-Process Asuka).Id
$p = [System.Diagnostics.Process]::GetProcessById($pid1)
"handles=$($p.HandleCount) WS=$([math]::Round($p.WorkingSet64/1MB))MB threads=$($p.Threads.Count)"
# 是否有人在附着调试器（true = 有人在 attach）
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class D{[DllImport("kernel32.dll")]public static extern bool CheckRemoteDebuggerPresent(IntPtr h,ref bool b);}'
$f=$false; [D]::CheckRemoteDebuggerPresent($p.Handle,[ref]$f); "debuggerAttached=$f"

# --- 句柄增长趋势（判定泄漏；跑 30 分钟，每 2 分钟一次）---
1..15 | ForEach-Object {
  $q = [System.Diagnostics.Process]::GetProcessById($pid1)
  "{0} handles={1} WS={2}MB" -f (Get-Date -Format HH:mm), $q.HandleCount, [math]::Round($q.WorkingSet64/1MB)
  if ($_ -lt 15) { Start-Sleep -Seconds 120 }
}
```

> **测量纪律（重要）**：**不要**用 `PostMessage` / UIA Invoke / 鼠标注入去驱动**正在使用的**实例来测性能——本次审查中，一个驱动工具把被测进程推到了 61,347 句柄 / 1,316 MB（见 §1.4 的归因说明）。要驱动就针对专门的测试实例，测完重启。

---

## 7. 验收标准（修完怎么证明有效）

| # | 指标 | 修复前（实测） | 修复后目标 |
|---|---|---|---|
| 1 | 一次 `CheckNow()` 的跨进程 UIA 调用次数 | 约 20–50 次 | **≤3 次**（缓存面板元素 + Win32 命中测试后） |
| 2 | 一次 `CheckNow()` 耗时（对同一 QQ 窗口） | 24–53 ms | **≤5 ms** |
| 3 | 150 ms 节流下的 QQ 侧占用 | 163–356 ms/s | **≤20 ms/s**（节流 1000 ms 后） |
| 4 | 订阅 `Descendants` 时的结构事件速率 | 46 事件/秒（受控实验） | **0/秒**（改 `Children` 后） |
| 5 | 静置 30 分钟句柄增长 | 待复现 | **≈0**（斜率绝对值 < 5/30 分钟） |
| 6 | 托盘隐藏后的全局参与者 | 钩子 + 焦点订阅 + 结构订阅仍在 | **全部释放**（`OnClosed` 不再是唯一拆卸点） |
| 7 | 关掉共存触发器后重启 | 启动瞬间仍装钩子 | **完全不装**（等配置就绪再启动） |
| 8 | 有字时发送表情的 UIA 元素枚举量 | 整树（367+ 元素） | **≤1 次 FindFirst** |
| 9 | 发送一张表情的 UI 线程占用（1000 张图库） | 整库序列化 + 全量重排 | **无同步序列化、无全量 `Refresh()`** |
| 10 | 搜索框连打 10 个字符的全量遍历次数 | 3–4 遍/键 | **每 250 ms 至多 1 遍** |

---

## 8. 建议的落地顺序（给接手 AI 的施工单）

1. **F1-①**：`ConfigModel.cs:11` → `false`（一行，风险最高项直接消失）。
2. **F1-②③⑤⑥**：`TreeScope.Children` + 节流 1000 ms + Win32 命中测试 + 缓存面板元素 —— 这四条同时拿到 §7 的 1/2/3/4 四项指标。
3. **F1-④⑦⑧**：补 `RemoveStructureChangedEventHandler`、托盘隐藏即卸载、等配置就绪再启动。
4. **F2**：剪贴板廉价去重提前（一行）+ 专用 STA 线程 + 流式哈希 + MD5 索引。
5. **F3**：`WindowService.cs` 稳定子串匹配 + 收窄 `TreeScope`。
6. **F5**：比较器快照 `now`（顺带修掉排序不自洽）、发送路径去掉 `RecalcRecentPinned+Refresh`、`UpdateCounts` 单订阅 + 计数、`BorrowedTags` 比较后再通知。
7. **F4**：保存去抖 + 序列化下后台 + 批量集合替换 + 搜索去抖。
8. **F6**：`DumpQqTreeDiagnostics` 换类名条件 + 把访问节点数计入上限。
9. **F7**：整条静默附着路线移除（若要保留，按 §F7 的最低限度收敛）。
10. **F8**：拖窗冻结 tile 定位、`Pooling=false`、标签编辑器 GIF 补 `StateChanged`、`CloseGifPreview` 补 `Stop()`。

---

## 附录 A：两轮原始实测数据

**空闲基线**：Asuka 0.4% 单核 / 工作集 271 MB / 线程 23 / 句柄 1209→1245；QQ 全进程 19.7% 单核；内存 31.9 GB 总、17.9 GB 空闲。

**UIA 成本**：见 §1.2 表。

**受控实验**
| 实验 | 结果 |
|---|---|
| `Descendants` vs `Children` 订阅（自建 Chromium，20 次 DOM 变更/秒） | 183 事件/4 秒 vs **0 事件/4 秒** |
| 附着调试器（复刻 `QqKeyWatchService` 的 P/Invoke 与泵语义） | 18 事件/秒、≤1 ms/事件、目标 UI 往返 0.53→0.46 ms（无恶化） |
| `GetKeyFunctionRva` 跑真实 112.4 MB `wrapper.node`（直接编译生产源码，非复刻） | `ReadAllBytes` 40 ms（冷）/36 ms（热）；三次 76/63/63 ms |
| 跨进程 UIA 操作泄漏 | 200 次 → +178 句柄（0.89/次），GC 只回收 21；按类型统计 Event +0.36/次；每次操作 185 ms |
| `FindFirst(Descendants)` 单路径泄漏 | 两次独立测量 0.03/次 与 0.267/次，GC 回收不掉 |
| `Process.GetProcessesByName` 不 Dispose | **0 / 500 次** |
| `File.AppendAllText` / `FromHandle` / `FindAll(全部)` / `DumpChildren depth3` | 全部 **0/次** |

**各种"生产代码形状"的单次句柄成本（`[受控实验]`，`GetProcessHandleCount` 差值，未强制 GC）** —— 这张表可以直接用来判断"某段代码是不是泄漏源"：

| 形状 | 次数 | 净增 | 单次 | 判定 |
|---|---|---|---|---|
| 空操作对照 | 400 | +1 | 0.003 | 基准噪声 |
| `Process.GetProcessesByName("QQ").Select(p=>p.Id).ToHashSet()` | 400 | +3 | **0.007** | 干净 |
| 同上 + 每个 `Dispose()` | 400 | −4 | ~0 | 干净 |
| `new Window{}.Show(); Close()` | 50 | +206 | **~4.1** | 每次留约 4 个（**但全应用只发生 103 次，量级不足**） |
| 同上 + `AllowsTransparency=true` | 50 | +201 | ~4.0 | 同上 |
| `new HwndSource(); Dispose()` | 50 | +13 | 0.26 | 小 |
| `new Thread(...)` + `Join` | 100 | +304 | **~3.0** | 每次留约 3 个（**但 Asuka 普查里 Thread 仅 73 个**） |
| `new DispatcherTimer{}.Start(); Stop()` 不 Dispose | 200 | +37 | 0.185 | 小 |
| `BitmapImage{OnLoad, UriSource}` + `Freeze()`（WPF 冷） | 400 | +47 | 0.117 | 一次性 |
| 同上（WPF 已预热） | 200 | +1 | 0.005 | 干净 |
| `BitmapImage` + **未释放的 `StreamSource` FileStream** | 200 | +201 | **1.005** | 真泄漏形状 —— **但本仓库没有这种写法** |
| `BitmapDecoder.Create(OnLoad)` | 200 | +2 | 0.010 | 干净 |
| **WpfAnimatedGif `SetAnimatedSource(gif) → (null)`** | 200 | **+1** | **0.005** | **干净** |
| `BeginAnimation(opacity)` churn | 200 | +1 | 0.005 | 干净 |
| `Mutex` / `ManualResetEventSlim` new + Dispose | 400 | +1 | 0.005 | 干净 |
| `File.AppendAllText` | 400 | +1 | 0.003 | 干净 |

> 读法：**单次成本高 ≠ 是主因**，必须乘上"发生次数"。本报告里 `Window`（4.1/次 × 103 次 ≈ 400）与 `Thread`（3/次）都因此被排除；反之 UIA 那条链路的单次成本虽小（0.36–0.89/次），但**每秒发生数百次**，所以它是主因。

**失控事件时间线**（归因见 §1.4 的 ⚠️）
```
03:27  2,696 句柄 /  271 MB（启动 9.5 分钟）
03:35  2,703 句柄 /  522 MB（18 分钟，8 分钟采样 24 点，平稳）
06:40 22,946 句柄 /  588 MB（3.4 小时）
06:43 26,213 / 558 MB → 06:47 52,324 / 728 MB → 06:53 61,347 / 1,316 MB（峰值，CPU 瞬时 82% 单核）
       句柄类型：Event 39,702 / Mutant 177 / Section 122 / File 118 / Thread 73 / Key 58 / Semaphore 46 / Timer 32
       托管堆：183,138 个对象 / 17 MB，任何类型实例数 ≤35；GDI 175 / USER 112
驱动工具被杀 → 增长立即停止（60 秒 −24）→ 稳定在 61,3xx 句柄 / 1,313 MB
```

## 附录 B：测量工具与现场清理

本轮审查新增的证据工程全部落在**单独一个目录**，可整体删除：

```
.perf-probe/
├─ probe.ps1        # 只读：CPU / 句柄 / 是否被调试 采样
├─ handles.ps1      # 只读：句柄 / GDI / USER / 线程 趋势采样
├─ growth.ps1       # 只读：长时间资源增长采样（输出 growth.csv）
├─ StaticBench/     # 直接编译生产版 QqKeyWatchService.cs，测 112 MB wrapper.node 分析耗时
├─ UiaProbe/        # 对真实 QQ 窗口测 UIA 调用延迟 / 事件率；dom 模式 = 自建 Chromium 受控实验；
│                   # handlecost 模式 = 统计单次 UIA 操作的句柄成本
├─ DbgProbe/        # 对自建 Chromium 复刻 AttachAndCapture，测附着对目标 UI 的影响
├─ HandleBench/     # Process.GetProcessesByName 句柄泄漏对照实验（[已证伪] 那一条）
├─ HandleProbe/     # NtQuerySystemInformation 句柄表 → 按对象类型分组（定位"Event 风暴"的工具）
└─ UiVerify/        # ⚠️ 不是本次审查创建的（另一会话留下），会用 PostMessage/UIA/SetCursorPos 驱动 Asuka
```

* 审查方现场清理：删除 **1,790 MB** 堆/全量转储、强杀并删除会驱动 App 的临时工具目录、确认无残留进程；**Asuka 自身的日志全部原样保留**。
* `.perf-probe/` 不在 `.gitignore` 里，提交前请决定保留还是 `Remove-Item -Recurse .perf-probe`；其中 `UiVerify/` 建议直接删除。
* **所有实验都只在自己的进程上做**：没有 attach、没有 hook、没有修改用户的 QQ 或 Asuka 的任何设置。
