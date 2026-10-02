# 飞鸟 Asuka 性能体检报告

> 体检对象：`D:\EasyGame\Source\OICQStickerManager`（工作树 = commit `f672e71`）
> 现场机器：12 逻辑核 / 32 GB RAM / Windows，QQ NT 9.9.36（`F:\SoftWare\QQ`，8 个 `QQ.exe` 进程）
> 体检方式：**全部只读**。代码审查 + 在本机对**真实运行中的 Asuka 与 QQ** 做只读测量 + 两个受控实验（自建进程，不碰你的 QQ）
> 所有引用行号取自当前工作树文件（注意：`MainWindow.xaml.cs` 实为 2100 行、`MainViewModel.cs` 实为 1699 行，比交接信息里的行数多）

---

## 0. 一句话结论

**"关闭软件才缓解"不可能来自持续 CPU 占用** —— 实测 Asuka 空闲时只吃 **0.4% 单核**，工作集 271 MB，15 秒内 UIA 事件 **0 个**，机器还有 17.9 GB 空闲内存（无换页压力）。

真正可疑的是 Asuka 把自己变成了**三个"全局参与者"**，并且全部**默认开启**：

| # | 全局参与行为 | 代码位置 | 默认值 |
|---|---|---|---|
| 1 | 全系统低级鼠标钩子 `WH_MOUSE_LL` | `QqPanelWatcher.cs:142` | `EnableQqCoexistTrigger = true`（`ConfigModel.cs:11`） |
| 2 | 全系统 UIA 焦点事件订阅 + 对**每个可见 QQ 窗口**订阅 `TreeScope.Descendants` 结构变化 | `QqPanelWatcher.cs:166` / `:551` | 同上 |
| 3 | 全系统剪贴板监听，且处理逻辑**跑在 UI 线程**上（含阻塞式 OLE 读取） | `MainWindow.xaml.cs:485` / `:582-650` | `CaptureClipboardImages = true`（`ConfigModel.cs:50`） |

关于你怀疑的**"深度同步抓 QQ 密钥时启动的第三方脚本"**——结论是**方向对了一半，但不是本次卡顿的主因**，详见 §3。我把它单列，因为它的**危险程度**远高于卡顿。

---

## 1. 实测数据（先看数字，再看结论）

### 1.1 运行态基线

| 指标 | 实测值 | 说明 |
|---|---|---|
| Asuka 空闲 CPU | **0.4%** 单核（8 秒内 31 ms） | 无空转、无 CPU 燃烧 |
| Asuka 工作集 / 私有内存 | 271 MB / 242 MB | |
| Asuka 线程 / 句柄 | 23 / 1209→1245 | 句柄数偏高但稳定 |
| QQ 全进程 CPU（8 个） | 19.7% 单核 | QQ 自身基线 |
| 系统内存 | 31.9 GB 总 / **17.9 GB 空闲** | **排除内存压力/换页导致的整机卡** |
| 系统提交 | 56.9 GB 上限 / 38.75 GB 空闲 | 同上 |

### 1.2 UI Automation 真实成本（对着**运行中的 QQ 窗口**测）

| 动作 | 实测 | 对应代码 |
|---|---|---|
| `FindFirst(TreeScope.Descendants, Class=sticker-panel)` | **avg 17.9 ms**（med 17.8 / max 19.2） | `QqPanelWatcher.cs:625` |
| `AutomationElement.FromPoint` + ≤12 级 `GetParent` | **avg 6.3 ms** | `QqPanelWatcher.cs:649-660` |
| `FindAll(Descendants, TrueCondition)`（发送兜底路径） | **avg 52.8 ms**（367 个元素） | `WindowService.cs:98` |
| **一整套 `CheckNow()`（1 个可见 QQ 窗口）** | **avg 24.4 ms / med 17.2 / max 72.2** | `QqPanelWatcher.cs:595-641` |
| 空闲 15 秒内结构变化事件 | **0 个** | `AddStructureChangedEventHandler` |
| 空闲 15 秒内全系统焦点事件 | **0 个** | `AddAutomationFocusChangedEventHandler` |

推论：以 150 ms 节流上限 6.7 次/秒计算 → **163 ms/s 花在 QQ 进程内部（≈16% 单核当量）**，而且这是**同步的跨进程调用**，QQ 必须停下来服务它。多个可见 QQ 窗口（聊天窗 + 主面板）按窗口数**线性放大**。

### 1.3 受控实验 A：`TreeScope.Descendants` 到底订阅了什么

我自己起一个隔离的 Chromium 窗口（独立 `--user-data-dir`，测完杀掉），让页面每秒改 20 次 DOM：

| 订阅方式 | 4 秒内事件数 | 结果 |
|---|---|---|
| **不订阅** | 0 | |
| `TreeScope.Descendants`（= Asuka 的写法） | **183（≈46 次/秒）** | DOM 抖动被逐条放大成跨进程事件 |
| `TreeScope.Children` | **0** | 缩窄范围即可消除 |

### 1.4 受控实验 B：附着调试器本身有多贵（复刻 `QqKeyWatchService` 的 P/Invoke 与泵语义）

| 指标 | 实测 |
|---|---|
| 调试事件量 | **18 次/秒**（LOAD_DLL 155 / CREATE_THREAD 59 / EXCEPTION 1 …） |
| 单个事件处理耗时 | **≤1 ms** |
| 目标 UI 线程消息往返延迟 | 0.53 ms → **0.46 ms（没有恶化）** |

**这是一条重要的证伪结论**：单纯"附着一个调试器"在实测中几乎不产生卡顿。卡顿只出现在下面三个具体时刻（详见 §3）。

### 1.5 受控实验 C：112.4 MB `wrapper.node` 静态分析耗时

用**生产代码本体**（`<Compile Include="Services\QqKeyWatchService.cs">`，不是复刻）跑真实 QQ 的 `wrapper.node`：

```
size : 117,866,024 bytes (112.4 MB)
ReadAllBytes        : 40 ms      (冷) / 36 ms (热)
GetKeyFunctionRva#1 : 76 ms  -> function RVA 0x21EBE20
GetKeyFunctionRva#2 : 63 ms
GetKeyFunctionRva#3 : 63 ms
```

关键不在 63~76 ms 这个绝对值，而在于**这段时间 QQ 是完全冻结的**（见 §3.2）。

---

## 2. 问题清单（按"能否解释你看到的症状"排序）

### 🔴 P0-1　默认开启的全局输入/自动化参与（最可能解释"关掉才缓解"）

**代码**：`QqPanelWatcher.Start()` 一次装三样东西，且在 `OnSourceInitialized` 就被调用：

```csharp
// Views\MainWindow.xaml.cs:481
protected override void OnSourceInitialized(EventArgs e)
{
    ...
    UpdateWatcherState();          // ← 装全局鼠标钩子 + 全局 UIA 订阅
}

// Views\MainWindow.xaml.cs:726
if (vm.EnableQqCoexistTrigger)     // 默认 true（ConfigModel.cs:11）
{
    _panelWatcher ??= CreatePanelWatcher();
    _panelWatcher.SetPollingFallback(vm.EnableWatcherPolling);
    _panelWatcher.Start();
}
```

```csharp
// Services\QqPanelWatcher.cs:139-173
_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);   // :142
...
Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged);                      // :166  全系统
...
// Services\QqPanelWatcher.cs:551   对每个可见 QQ 窗口
Automation.AddStructureChangedEventHandler(el, TreeScope.Descendants, OnStructureChanged);
```

**为什么这会让你觉得"整机卡"**：

1. **鼠标钩子=把 Asuka 放进了全系统的鼠标输入链**。每个鼠标事件都要先绕到 Asuka 的钩子线程再放行。一旦这个线程被 GC / UIA 调用 / 磁盘日志拖住，**整个桌面的鼠标输入都会被延迟**；超过系统 `LowLevelHooksTimeout` 时 Windows 会**静默摘掉**钩子（代码注释 `:137-138` 自己承认"实测发生过"）。
2. **UIA 是同步跨进程调用**。实测一次完整验证 24 ms，而这 24 ms 里 QQ 必须响应 —— 用户正在 QQ 里操作时，正是事件最密、验证最频繁的时候。
3. **点击 QQ 表情按钮会触发一次"验证风暴"**，而这一刻用户正准备打字：

```csharp
// QqPanelWatcher.cs:400   按钮点击后 7 次全量扫描
foreach (var delay in new[] { 0, 80, 160, 240, 320, 480, 700 }) { ... CheckNow(); }

// QqPanelWatcher.cs:427-447  2.5 秒内每 200 ms 再扫一次 → 12 次
private void ArmTailVerify() { ... TailVerifyIntervalMs = 200 ... }
```

合计 **~19 次全量跨进程扫描 / 3.2 秒 ≈ 460 ms 花在 QQ 进程内部**。

4. **托盘隐藏后不会卸载**：唯一的拆卸点是 `OnClosed`（`MainWindow.xaml.cs:808`）。关到托盘 = 钩子和订阅继续挂着。

**建议的最小修法**（按性价比排序）：

```csharp
// ① 默认关：ConfigModel.cs:11 改为 false —— 一行改动消除全部风险
public bool EnableQqCoexistTrigger { get; set; } = false;

// ② 缩窄订阅范围（实测 Descendants→Children 让事件数 46/s → 0/s）
Automation.AddStructureChangedEventHandler(el, TreeScope.Children, OnStructureChanged);

// ③ 节流 150ms → 1000ms，并在 QQ 不在前台时直接跳过验证
private const int VerifyMinIntervalMs = 1000;

// ④ 托盘隐藏时停掉，恢复时重建
protected override void OnStateChanged(EventArgs e) { /* Hidden → _panelWatcher?.Dispose(); */ }

// ⑤ 鼠标钩子改为"仅在 QQ 前台时安装"，或彻底去掉（乐观打开可由焦点事件兜底）
```

另外注意配置加载的**竞态**：`MainViewModel()` 构造里是 `_ = LoadConfigAsync();`（`MainViewModel.cs:211`）——**不等待**。所以即使用户在配置里把共存触发器关成 `false`，`OnSourceInitialized` 先读到的仍是字段默认值 `true`，**钩子照样会被装上一次**，直到配置加载完再拆掉。正确做法是**启动时先同步读一次 config**（项目已有先例：`App.ReadSilentStartFlag()`，`App.xaml.cs:110`），或者让 watcher 等一个"配置就绪"信号再启动。

---

### 🔴 P0-2　剪贴板监听：全系统参与者 + UI 线程阻塞调用

**代码**：`AddClipboardFormatListener`（`MainWindow.xaml.cs:485`）→ 任意进程写剪贴板都触发 `WM_CLIPBOARDUPDATE` → 300 ms 去抖 → `EvaluateClipboardCapture()` **同步跑在 UI 线程**：

```csharp
// Views\MainWindow.xaml.cs:582
private void EvaluateClipboardCapture()
{
    var data = Clipboard.GetDataObject();          // :590 ← 阻塞式 OLE 读取，可挂数秒
    ...
    else if (Clipboard.ContainsImage())
    {
        var src = Clipboard.GetImage();            // :609
        encoder.Save(ms);                          // :615 PNG 编码
        File.WriteAllBytes(path, pngBytes);        // :621 落盘
    }
    ...
    var md5Hex = Convert.ToHexString(MD5.HashData(
        pngBytes ?? File.ReadAllBytes(path)));     // :635-636 ← 整文件读入 + 哈希
    if (vm.Stickers.Any(s => s.Md5 == md5Hex))     // :637 ← O(图库)
    ...
    if (sig == _lastClipboardSig) return;          // :644 ← 去重判断在最后！
}
```

**为什么危险**：

* `Clipboard.GetDataObject()` 是**跨进程阻塞**调用，且剪贴板是**全系统串行资源**。持有剪贴板的应用不响应时，Asuka 的 UI 线程会卡住，同时**其他所有应用的复制/粘贴也被拖住**。本机还开着 `mstsc`（远程桌面），RDP 剪贴板重定向是这类挂起的经典来源。
* 代码自己在 `:629` 注释里写明"**部分应用会周期性重写相同剪贴板内容**"——也就是这条路会被**周期性反复触发**，每次都做全量哈希 + O(n) 扫描。
* 去重签名（`:644`）排在**哈希和 O(n) 扫描之后**，等于把最贵的活干了再判断要不要干。
* 300 ms 去抖是**重置式**的（`:560-562`）：若某应用每 <300 ms 重写一次剪贴板，这个定时器**永远不会到期**，功能静默失效（顺带掩盖了性能问题）。

**建议**：

```csharp
// ① 先去重，再做重活；用廉价签名
if (sig == _lastClipboardSig) return;
// ② 整体挪出 UI 线程
await Task.Run(() => { ... });           // OLE 读取必须在 STA 线程 → 改用专用 STA 线程
// ③ 流式哈希，别整体读入
MD5.HashData(File.OpenRead(path));
// ④ 图库 MD5 用 HashSet 预建索引，别每次 Any()
```

> 补充：OLE 剪贴板 API 要求 STA。当前跑在 WPF UI 线程（STA）上是"正确"的，但也正是它**卡住 UI 的原因**。建议开一个专用 STA 线程干这件事，别占用 UI 线程。
> 另：`ClipboardToastWindow` 的 `_autoDismiss` 定时器没有 `OnClosed` 清理（`ClipboardToastWindow.cs:132`），而 `MainWindow.xaml.cs:663` 是直接 `old.Close()` —— 每次捕获泄漏一个定时器 + 一个已关闭窗口的强引用。

---

### 🟠 P1-1　深度同步的"静默附着调试器"路径（你的怀疑对象，见 §3 专项）

### 🟠 P1-2　发送兜底路径会枚举 QQ 整棵 UIA 树

```csharp
// Services\WindowService.cs:92
var editor = root.FindFirst(TreeScope.Descendants,
    new PropertyCondition(AutomationElement.ClassNameProperty,
                          "ProseMirror ExEditor-qq-msg-editor is-empty"));
if (editor == null)
{
    // 输入框非空时 class 会去掉 is-empty 后缀，按包含匹配兜底
    var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);   // :98 实测 52.8 ms
    foreach (AutomationElement e in all) { /* 每个元素一次跨进程属性读 */ }
}
```

**关键**：快速路径匹配的 class 里带 `is-empty`，**输入框里只要有字就匹配不上** → 每次"聊天框已有内容时发送表情"都会走整树枚举。改成匹配稳定的子串（如 `ExEditor-qq-msg-editor`）或 `ControlType.Document`、并把范围收到编辑器的父节点即可。

### 🟠 P1-3　Asuka 自己的 UI 线程做与图库规模成正比的重活

| 位置 | 问题 | 触发频率 |
|---|---|---|
| `MainViewModel.cs:1598-1599` | 发一次表情 = 全库 `JsonSerializer.Serialize`（`WriteIndented`）+ 写 tmp + 拷 `.bak` + Move + `_stickersView.Refresh()`（全量重过滤+重排序） | **每次发送** |
| `MainViewModel.cs:1145-1146` | 搜索框**每敲一个键**都 `Refresh()` + `UpdateTabTags()`（`Clear()` 后逐个 `Add`，两个集合各来一遍） | 每次按键 |
| `MainViewModel.cs:791 / 814-815/207-208` | 启动时 `FirstOrDefault` 做 O(n²) 路径匹配；随后逐个 `Add`，而每个 `Add` 触发 `UpdateCounts()` 又遍历整个视图（再一个 O(n²)） | 每次启动 |
| `MainViewModel.cs:685-689` | 每个镜像条目 `Stickers.Any(s => s.Md5 == ...)` → O(镜像 × 图库)，在 UI 线程 | 每次 QQ 收藏变化 |
| `QqEmojiService.cs:130-151` | `Mirror.Clear()` + 441 次 `Add`（绑定在 ItemsSource 上）无批处理 | 每次重扫 |
| `Services\ImageSniffer.cs:115-121` | 每个 WebP 文件 new 一个 `BitmapImage` 探测解码能力，且每个 WebP 被解码两次 | 每次导入 |
| `Models\StickerModel.cs:74-100` | `_imageSource` 位图缓存**永不淘汰**，浏览越多涨越多（实测工作集 271 MB） | 滚动图库 |

> 这些在当前数据量（`Library` 125 个文件、`Ori` 441 个文件）下都不致命，但**都随图库规模线性/平方增长**，是"用得越久越卡"的典型来源。修法：保存去抖 + 序列化下后台 + 去掉 `WriteIndented`；搜索去抖 250 ms；`Stickers` 换成字典索引 + 批量替换集合；位图缓存改 LRU 或 `DecodePixelWidth=128`。

### 🟡 P2-1　玻璃材质 / 合成器开销

* `GlassWindow.cs:40-51`：`LocationChanged` 每帧 → 3 次 `SetWindowPos`（3 个独立 DWM 模糊窗口重定位）；拖动窗口 = 每帧重算 ~1.2 Mpx 背景模糊。建议拖动期间冻结定位，松手后一次性摆位。
* `GlassWindow.cs:166-183`：`SyncTileVisibility` = 6 次 `SetWindowPos` + 3 次 `SetWindowCompositionAttribute`；而 `BlurTile.Show(false)` 里的 `ClearAccent()` 是**故意清掉 accent 逼 DWM 下次重算模糊**的。每次热键/共存打开都要付一遍。
* `MainWindow.xaml:8` 等 3 个窗口 `AllowsTransparency="True"` → 走 `UpdateLayeredWindow` 分层窗口路径，任何一帧动画都要整窗重推。
* GIF 气泡的 `DropShadowEffect BlurRadius=24`（`MainWindow.xaml.cs:1301`）在透明 Popup 里**逐帧重算模糊**。

> 本机 DWM 基线本来就不轻（Wallpaper Engine `wallpaper64` 450 MB、NVIDIA Overlay、Razer Synapse），这类合成器开销更容易被放大成"整机不跟手"。

### 🟡 P2-2　深度同步临时目录泄漏（**已实测确认**）

`%TEMP%\asuka-deepsync\` 残留 **3 个目录 / 2.37 MB**（各含 `plain.db` 776 KB + `plain.db-shm` 32 KB），来自 `QqDeepSyncService.cs:89-115`：`finally` 里 `Directory.Delete(tempDir, recursive: true)` 被 `catch { }` 静默吞掉。

**机制修正**（交接材料里说"`SqliteConnection` 未 Dispose"是**不准确**的）：`ReadFavMd5s` 用的是 `using var conn`（`QqDeepSyncService.cs:121`），确实 Dispose 了；真正的原因是 **Microsoft.Data.Sqlite 默认开启连接池**，`Dispose()` 只把连接还池、**不释放底层文件句柄**，于是 `plain.db` / `-shm` 删不掉。修法：连接串加 `Pooling = false`（或删目录前 `SqliteConnection.ClearPool`）。

### 🟡 P2-3　启动路径

`public static MainViewModel SharedViewModel { get; } = new();`（`App.xaml.cs:18`）是**静态初始化器**，在 `OnStartup` 里的单实例互斥判定（`:56`）**之前**执行。第二个实例（双击图标、开机自启撞车）会先跑完图库 + 配置加载，再发现自己不是首实例然后退出。

---

## 3. 专项：关于"深度同步抓 QQ 密钥导致卡顿"（你的初步怀疑）

**结论：机制真实存在且有严重隐患，但"附着一个调试器"本身我实测不出足以解释整机卡顿的开销。** 逐条交代：

### 3.1 它是怎么工作的

```csharp
// QqKeyWatchService.cs:55-80   600 ms 轮询，每个新出现的 QQ.exe 都派一个独立任务
foreach (var p in Process.GetProcessesByName("QQ"))
{
    if (tried.Contains(p.Id)) continue;
    tried.Add(p.Id);
    _ = Task.Run(() => TryAttachAsync(pid, ct), ct);      // :73
}
...
// :82-105
var key = await Task.Run(() => session.AttachAndCapture(TimeSpan.FromSeconds(45), ct), ct);
// :127-158
if (!Native.DebugActiveProcess((uint)_pid)) throw ...     // :129
Native.DebugSetProcessKillOnExit(false);                  // :132
```

**本机日志实证它真的跑过**（`%TEMP%\asuka-watcher.log`）：

```
[23:17:28.329] keywatch: watcher started, 12 running QQ process(es) skipped
[23:17:30.795] keywatch: attaching to new QQ process 6532
[23:17:31.441] keywatch: detached from 6532
[23:17:31.442] keywatch: no key from 6532 (already logged in or not the UI process); detached
[23:17:52.640] keywatch: attaching to new QQ process 30736
[23:17:52.641] keywatch: attach failed: 拒绝访问。
```

### 3.2 实测：附着调试器有多贵？

我用自己的 Chromium 进程（**没有碰你的 QQ**）复刻了完全相同的 P/Invoke 层与事件泵语义：

| 指标 | 实测 | 解读 |
|---|---|---|
| 调试事件量 | 18 次/秒 | 不算高 |
| 单事件处理 | ≤1 ms | 占用 ≈2% |
| 目标 UI 线程往返延迟 | 0.53 ms → 0.46 ms | **没有恶化** |

→ **"附着调试器"本身不足以解释你描述的严重卡顿。** 这一条对你的怀疑是**否定性证据**，我不打算把它说成主因。

### 3.3 但它在三个具体时刻会真正冻结 QQ

1. **附着瞬间**：`DebugActiveProcess` 会为目标进程**已存在的每个线程和模块合成调试事件**。实测本机 QQ 主进程 **203 个线程 / 2854 个句柄**（`QQ` pid 24696），这些线程在事件刷完之前全部冻结。
2. **`StaticAnalysis.GetKeyFunctionRva` 期间**：它是在 `LOAD_DLL_DEBUG_EVENT` 分支里被调用的（`QqKeyWatchService.cs:254-263`），也就是**调试事件还没 `ContinueDebugEvent`、目标进程处于挂起状态时**去 `File.ReadAllBytes` 一个 112.4 MB 的文件再扫两遍。实测 63~76 ms（NVMe + 页缓存命中）；**冷缓存 / 机械盘 / 被杀软拦一道时是数百毫秒到数秒的 QQ 完全冻结**。
3. **INT3 命中时**（`SetBreakpoint`，`:200-214` 往 `wrapper.node` 的 `.text` 写 `0xCC`）：每次命中都要走「异常 → OpenThread → GetThreadContext → 改 RIP → ReadProcessMemory → SetThreadContext → 单步 → 单步异常 → 清 TF → 重装 0xCC → Continue」一个完整来回，**其间 QQ 全部 203 个线程都是停的**。而断点下在 `nt_sqlite3_key_v2` 相关的密钥函数上 —— 那是**每次打开加密库都会走**的路径。

### 3.4 比卡顿更该修的三件事（都在这条路径上）

| 风险 | 位置 | 说明 |
|---|---|---|
| **往 QQ 内存写 `0xCC`** | `QqKeyWatchService.cs:200-214` | 在生产进程代码段里打补丁。任何一步失败/被中断都可能让 QQ 崩在非法指令上 |
| **trap flag 未清理就分离** | 成功分支 `:350-353` → `Detach()` `:400-407` | 命中并拿到密钥后 `SetSingleStep` 给线程置了 TF，随后直接 `Detach()`（只还原了 `0xCC`，**没清 TF**）→ `DebugActiveProcessStop` 后该线程以 TF=1 恢复，可能抛出无人处理的 `STATUS_SINGLE_STEP`。**这条路径在本机从未被触发过（日志里没有 `key found at breakpoint`），属于潜伏缺陷，我没有实测验证，建议按高风险对待** |
| **不可取消、不可控的附着窗口** | `:82-105`、`:229-237` | 每个新 QQ.exe 起一个 45 秒任务；`Dispose()` 不取消在途任务；`tried` 只增不减；QQ NT 一次启动就 8 个进程 → 最多 8 个并发调试会话。`_captured` 只有在**真的抓到密钥**后才停轮询，所以一次失败会**永久武装**在下一次 QQ 启动上 |

### 3.5 我对这条路径的建议

1. **去掉"静默附着"这条路线**，只保留用户显式点击后运行官方脚本（`QqKeyExtractor`）的那条路。理由：它要往第三方进程内存写代码、要在挂起窗口里做 112 MB 磁盘 IO、没有单飞/退避/取消，收益（一个 16 字符密钥）与风险完全不成比例。
2. 如果一定要保留，最低限度：**单飞**（同一时刻只允许一个 attach）、**跳过 `--type=` 子进程**（只考虑主进程）、**附着时长从 45 s 降到 3~5 s**、**静态分析挪到附着之前**（先分析完文件再 attach，绝不在目标挂起时读盘）、**永远不要下 INT3**（改用一次性调试寄存器/或直接放弃），并且 `Dispose()` 必须能取消在途任务。
3. 把"密钥失效"的判定与"无限重试"解耦：现在密钥一空就永久武装 watcher，是最容易变成"长期隐形拖累"的形态。

---

## 4. 我查过但**没有**发现问题的方向（排除项）

避免你往错误方向排查，这些我明确验证过是干净的：

* **没有任何常驻高频定时器/空转循环**：全仓库没有 `CompositionTarget.Rendering`、`RenderTargetBitmap`、截屏、`RepeatBehavior="Forever"`、`while(true)` 空转；`MainViewModel.cs` 里 **没有** 任何 `Timer` / `Thread.Sleep` / 轮询循环。
* **没有全局键盘钩子**（只有鼠标钩子，`WH_MOUSE_LL=14`）。所以"打字延迟"不是键盘钩子直接造成的。
* **没有内存压力**：32 GB 内存空 17.9 GB，提交空 38.75 GB —— 不是换页导致的整机卡。
* **图库虚拟化正常**：`MainWindow.xaml:360-361`、`QuickPanelWindow.xaml:115-116` 都是 `IsVirtualizing="True"` + `VirtualizationMode="Recycling"`，没有被关掉。
* **空闲时 UIA 事件 0 个**：`QqPanelWatcher` 的类注释说"零常驻成本"，**这个说法在空闲态是成立的**（实测 0 事件 / 0.4% CPU）；问题在**活动态**（§2 P0-1）。
* **深度同步默认关**（`ConfigModel.cs:59 = false`），所以"新装用户一上来就卡"不能用它解释；只有开过该功能且密钥失效的用户才会踩到 §3。
* **RDP/桌面软件基线**：本机同时跑着 Wallpaper Engine、NVIDIA Overlay、Razer Synapse、HIPS 杀软、Everything、mstsc 远程桌面。这些本身就在抢 DWM 和 CPU —— 评估"是不是 Asuka 造成的"时必须扣掉这个基线，否则容易误判。

---

## 5. 建议的下一步（现场 A/B 验证，30 分钟内可完成）

我在受控环境里测得每个单项都不足以单独造成"严重卡顿"，说明**真实症状大概率是叠加 + 特定触发条件**。请在你的实际使用场景里按下面顺序做 A/B，一次只改一个变量：

| 步骤 | 操作 | 观察什么 | 若缓解则根因是 |
|---|---|---|---|
| 1 | 设置里**关掉「QQ 表情面板共存」**，重启 Asuka | QQ 打字是否跟手 | §2 P0-1（全局钩子 + UIA 订阅） |
| 2 | 再关掉「**复制图片后提示入库**」，重启 | 任意应用复制/粘贴是否变顺畅 | §2 P0-2（剪贴板全系统参与者） |
| 3 | 关掉「**QQ 收藏深度同步**」并**清空已保存密钥**，重启（这一步会停掉 keywatch） | QQ 启动/登录期间的卡顿是否消失 | §3（调试器附着） |
| 4 | 每次都记录下来 | `%TEMP%\asuka-watcher.log` 的增长速度、任务管理器里 Asuka 的 CPU/句柄曲线 | 用于区分"空闲卡"还是"操作时卡" |

补充观测手段（不改代码）：

```powershell
# Asuka 实时 CPU / 句柄 / 线程
Get-Process Asuka | Select-Object Id,@{n='CPU_s';e={[math]::Round($_.TotalProcessorTime.TotalSeconds,1)}},@{n='WS_MB';e={[math]::Round($_.WorkingSet64/1MB,0)}},HandleCount,@{n='Threads';e={$_.Threads.Count}}
# 是否有进程正被调试（true = 有人在附着）
# 见 .perf-probe\probe.ps1（只读，含 CheckRemoteDebuggerPresent）
```

---

## 6. 附录：复现工具与现场清理

本次体检新增的**可复现证据工程**全部落在单独一个目录，可整体删除：

```
.perf-probe/                              (1.4 MB, 未跟踪)
├─ probe.ps1            # 只读：CPU/句柄/是否被调试 采样
├─ StaticBench/         # §1.5：直接编译生产版 QqKeyWatchService.cs，测 112MB wrapper.node 分析耗时
├─ UiaProbe/            # §1.2 / §1.3：对真实 QQ 窗口测 UIA 调用延迟、事件率；dom 模式=自建 Chromium 受控实验
└─ DbgProbe/            # §1.4：对自建 Chromium 复刻 AttachAndCapture，测附着对目标 UI 的影响
```

* 复现需要的只有上面 4 个项目 + 本报告；`*/bin`、`*/obj` 是构建产物，可删。
* 所有实验均在**自己启动的进程**上做，测完已自行清理（Chromium 进程 + 隔离 profile 已删除）；**没有 attach、没有 hook、没有改任何设置**到你的 QQ 或 Asuka。
* 未跟踪目录 `.perf-probe/` 不在 `.gitignore` 里，提交前请决定是保留还是删除（`Remove-Item -Recurse .perf-probe`）。
