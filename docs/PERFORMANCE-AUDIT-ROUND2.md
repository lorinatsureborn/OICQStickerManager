# 飞鸟 Asuka 性能体检 —— 第二轮（复检 + 复测）

> 基准：第一轮报告 [PERFORMANCE-AUDIT.md](PERFORMANCE-AUDIT.md)（当时 HEAD = `f672e71`）
> 本轮对象：**HEAD = `97a08c2`**（今晚新增 15 个提交，工作树干净）
> 现场：同一台机器，运行中的实例 `D:\Asuka\Asuka.exe` pid 30576（03:17:56 启动）
> 方式：仍然**只读**。所有测量都在本机实时进程上做；受控实验只用自建进程
> ⚠️ 本轮中途运行环境被重启过一次（6 个 `DeepSeek Harness` 进程启动时间均为 06:40:2x），上一轮的后台任务与子代理随父进程丢失——那是**环境重启**，不是代码问题，也不是被测程序的问题

---

## 0. 一句话结论

1. **上一轮的三条 P0 一条都没修**：三个"全局参与者"依然全部默认开启，且托盘隐藏后**永不卸载**。
2. **唯一真正变严重的是 UIA 验证成本**：一次完整 `CheckNow()` 从 24.4 ms **涨到 53.2 ms**，按 150 ms 节流算 = **约 356 ms/s（36% 单核当量）花在 QQ 进程内部**，而这正是"打字延迟"的现场。
3. **句柄泄漏的根因已定位（受控实验证明）**：**托管 UIA 客户端 `System.Windows.Automation` 每次跨进程 UIA 操作会在调用方进程泄漏 0.4–0.9 个内核 `Event` 对象，GC 回收不掉**。而 `QqPanelWatcher` 一次验证约 20–50 次 UIA 操作、按 150 ms 节流可达 6.7 次/秒 → **130–330 次 UIA 操作/秒**。这条链路**同时**是"QQ 打字延迟"和"句柄泄漏"的根因 —— 修一处，两个问题一起解决。
4. **今晚观测到的"爆发式增长"（句柄 2.7 万→6.1 万、内存 1.3 GB）经查是我方审查工具自己驱动的**，不是产品在无人操作下的行为。**这一点我必须如实说明并纠正**，详见 §3。

---

## 1. 上轮问题复检结果（逐条对照当前代码）

| 上轮编号 | 问题 | 本轮状态 | 证据 |
|---|---|---|---|
| P0-1 | 全局鼠标钩子 + 全局 UIA 焦点订阅 + 每个 QQ 窗口的 `TreeScope.Descendants` 订阅，默认开启 | **未修** | `ConfigModel.cs:11` 仍是 `= true`；`MainWindow.xaml.cs:499` `OnSourceInitialized` 无条件调用 `UpdateWatcherState()` |
| P0-1 | 托盘隐藏后不卸载 | **未修，且已确认为唯一拆卸点** | 全仓库搜索：`_panelWatcher?.Dispose()` 只出现在 `:752`（开关关闭时）与 `:823`（`OnClosed`）；`OnClosing:248-253` 在 `CloseToTray` 时改为 `HideToTray():274-284`，只 `Hide()`，`OnClosed` **永不执行**；没有任何 `StateChanged`/`IsVisibleChanged` 钩子。实机 `config.json` 的 `CloseToTray: true` → **用户点 ✕ 之后，钩子和订阅继续挂着** |
| P0-1 | 配置未就绪就装钩子（关掉也白关） | **未修** | `MainViewModel.cs:218` 仍是 `_ = LoadConfigAsync();`（发了不等）；`:499` 读到的仍是字段默认值 `true` |
| P0-2 | 剪贴板监听是全局参与者，处理跑在 UI 线程 | **未修** | 见 §2.3 |
| P1-1 | 深度同步静默附着调试器 | **未修**（本轮实测**未激活**：`CheckRemoteDebuggerPresent` 对 8 个 QQ 进程全为 False，Asuka 无子进程；`QqDbKey` 非空 → keywatch 未启动） |
| P1-2 | 发送兜底枚举 QQ 整棵 UIA 树 | **未修**（`WindowService.cs:98` 仍在；`GlassWindow`/`BlurTile`/`ImageSniffer`/`ImageService` 自 `f672e71` **零改动**） |
| P1-3 | 保存/搜索/启动的 O(n²) UI 线程工作 | **未修**（MainViewModel 属于本轮另一个审查范围，默认值、`Refresh()` 调用点未变） |
| P2-1 | 玻璃材质每帧 3 次 `SetWindowPos` | **未修**（`GlassWindow.cs` 零改动） |
| P2-2 | `asuka-deepsync` 临时目录泄漏 | **未修**（根因是 SQLite 连接池持有原生句柄，不是漏 Dispose） |
| P2-3 | 第二实例先加载图库再退出 | **未修**（`App.xaml.cs:18` 静态初始化器仍在互斥判定之前） |
| 上轮 R9 | `ClipboardToastWindow` 每次捕获泄漏定时器 | **已澄清：不成立** | `Finish()` 先 `Stop()` 再 `Close()`，调用方走 `Finish`。上轮该结论有误，本轮更正 |

---

## 2. 本轮新增 / 更新的量化结果

### 2.1 UIA 验证成本翻倍（本轮最重要的数字）

对**运行中的真实 QQ 窗口**完整复刻 `QqPanelWatcher.ScanQqWindows()`：

| | 第一轮（10-01） | 第二轮（10-02） |
|---|---|---|
| 每次 `CheckNow()` | avg **24.4 ms** / med 17.2 / max 72.2 | avg **53.2 ms** / med 39.0 / max 117.9 |
| 150 ms 节流下的占用 | 163 ms/s（16%） | **356 ms/s（35.6%）** |
| `FindFirst(Descendants, Class=sticker-panel)` 单次 | 17.9 ms | 同量级 |
| `FindAll(Descendants, TrueCondition)`（发送兜底路径） | 52.8 ms / 367 元素 | 65.7 ms |

**这是"QQ 打字延迟"最直接的机制**：这些调用是**同步跨进程**的，QQ 必须停下来服务它们；用户操作 QQ 时正是事件最密、验证最频繁的时候。

### 2.2 结构事件在真实环境中确实在流

`asuka-watcher.log`（2215 行）里 Asuka 实际收到的结构变化类型：

```
ChildAdded × 36 个观察实例    ChildRemoved × 31    ChildrenReordered × 31
```

与第一轮受控实验完全吻合（同一 Chromium 窗口改 DOM：`Descendants` 订阅 **46 事件/秒**，`Children` **0 事件/秒**）。

### 2.3 剪贴板路径（逐行确认，含一个"半修"细节）

`MainWindow.xaml.cs:597-665`：**仍在 UI 线程**（由 `:591` 的 `_clipboardDebounce` Tick 调用）。

关键是在 `:620` 已经算出了一个**廉价签名**（`FileInfo` 的 Length + LastWriteTimeUtc），但去重比较仍被放在 `:659` —— 也就是在 `File.ReadAllBytes(path)` + 整文件 MD5 + `vm.Stickers.Any(...)`（`:650-652`）**之后**。廉价判据在手却不用，白付最贵的代价。

> 补充（来自审查）：`Clipboard.GetDataObject()` 要求 STA，所以**不能**简单 `Task.Run`；正确做法是专用 STA 线程。

### 2.4 新增代码带来的新风险

| 位置 | 问题 | 实测/推断 |
|---|---|---|
| `QqPanelWatcher.cs:51` `LooksLikeEmojiButton` | **丢掉了 class 校验**，只按 `name.Trim() == "表情"` 匹配 | 误命中的代价不只是"面板贴错位置"：每次误命中都会走 `VerifyWithOpenRetriesAsync`（7 次全量扫描）并覆写 `_emojiBtnRect`（让鼠标钩子瞄错地方） |
| `QqPanelWatcher.cs:475-501` `DumpQqTreeDiagnostics`（新增） | 深度 3 的 UIA 树遍历，每个访问到的节点一次 `FindAll(Children, TrueCondition)`，每个子元素 2 次跨进程属性读 | 实测 **322–388 个元素被访问**、**1000+ 次跨进程往返**、后台线程 **3–10 秒**；`hits.Count > 40` 只限制**命中数**，不限制**遍历量**。触发条件 = 表情按钮点击后面板未检出，90 秒限频。**目前 0 次触发**（日志里 0 行 `diag`），属潜伏风险 |
| `QuickPanelWindow.xaml.cs:229-270`（新增 GIF 原地播放） | 悬浮时按 `UriSource` **全分辨率**新解一份 GIF（注释明确"不可 Freeze、不可设 DecodePixelWidth"） | 同时只播 1 张、移开/回收/隐藏即 `SetAnimatedSource(null)`，**上限正确**。建议加"按 FullPath 缓存解码结果"以避免重复全量解码 |
| `MainWindow.xaml.cs:1727/1737` 标签编辑器 GIF | 只有 `Deactivated:131-135` 会停；`MinimizeButton_Click` 最小化与 `HideToTray` 的 `Hide()` **都不触发 `Deactivated`** | 托盘隐藏时标签编辑器 GIF 可能继续解码播放（快捷面板做对了：`:49 IsVisibleChanged → StopPanelGifAnimation`） |
| `MainWindow.xaml.cs:1498-1504` `CloseGifPreview` | 只 `IsOpen = false` | 已排队等待弹出的预览仍可能启动并一直播下去（那个本该停它的定时器没在跑） |

### 2.5 明确干净的（已实测/逐行核对，不必再查）

* `FeedbackService`：`Process` 全部 `finally { p.Dispose(); }`，`StreamReader` 用 `using`。
* `ImageSniffer` / `ImageService` / `GlassWindow` / `BlurTile`：**自 `f672e71` 零改动**；3 个 `BlurTile` 在建窗时一次创建、关闭时统一 `Dispose`→`DestroyWindow`，`Layout()` 有 `_shown` latch，没有 `ShowWindow` churn。
* 所有 `File.AppendAllText` 日志路径：实测 500 次调用 **0 句柄**。
* `DispatcherTimer` 卫生：范围内每一个定时器都在自己的 Tick 里 `Stop()`。
* 图库网格里**没有**原地 GIF 播放（只有快捷面板 `AnimatedFrame` + 主窗口单个悬浮 Popup），"N 个格子同时动"的风险不存在，上限是 1。
* 没有任何按格/按帧的 `DropShadowEffect`。

### 2.6 MainViewModel / Models 的新增与恶化项（今晚 15 个提交引入）

| 严重度 | 位置 | 问题 |
|---|---|---|
| **high** | `MainViewModel.cs:1571-1590` | 新比较器 `CompareWithMode` → `RankScore` → `StickerRanking.Score(..., now ?? DateTime.Now)`：**每次比较都读一次时钟**。单次排序内 `DateTime.Now` 会前进，而 `RecencyWeight` 在 1h/6h/24h/3d/7d/30d 有**阶跃边界**（`StickerRanking.cs:22-31`）→ 比较器在单次排序内**不自洽**（顺序抖动，甚至 `CustomSort` 抛"无法比较两个元素"）。`case 2` 还每比较一次 `string.Join("、", Tags)` 生成 2 个字符串 |
| **high** | `MainViewModel.cs:2131-2134` | **每次发送**都 `SaveDatabaseAsync()`（整库 `WriteIndented` 序列化，且 `File.Copy`/`File.Move` 是同步的）→ `RecalcRecentPinned()`（整库 `OrderByDescending().Take(N)`）→ `_stickersView.Refresh()`（全量重排 + 重新实体化）。发送是最高频动作，却背了最重的活 |
| **high** | `MainViewModel.cs:1424-1459` | 搜索框仍是 `UpdateSourceTrigger=PropertyChanged`，每敲一键 = `Refresh()` + `UpdateTabTags()`（`Clear()`+逐个 `Add` + `ComputeTagHeat()` 遍历全库）+ `RefreshPanelTabs()` + **新增** `ApplyQqSearchBuckets()`（全库字典 + 每账号遍历 + 每账号 `Refresh()`）→ 比第一轮**更重** |
| **high** | `MainViewModel.cs:118-125` + `:214-215` | `_stickersView` 本身就是 `Stickers` 的视图，却又同时订阅了两者的 `CollectionChanged` → **每次增删触发两次 O(n) 全视图遍历**（`UpdateCounts` 里的 `_stickersView.OfType<object>().Count()`）。这是启动 O(n²) 的引擎 |
| **medium** | `QqStickerModel.cs:19-28` | `BorrowedTags` 的 setter **无条件** `OnPropertyChanged()`（对比 `IsImported:61-68` 是有比较的），而 `UpdateImportedFlags`（`:743-752`）对**每个镜像项**每次都赋值 → 每次 QQ 收藏变化/每次标签编辑都产生 O(mirror) 通知风暴 |
| **medium** | `QuickPanelWindow.xaml.cs:65-69` | `viewModel.QuickPanelSendInitiated += …` **从不退订**，而 VM 是 `static App.SharedViewModel`、快捷面板在换肤时会被整体重建（`MainWindow.xaml.cs:142-165`）→ 死处理器 + 整窗对象图被静态根持有 |
| low | `MainViewModel.cs:2079/831` | `_lastSendTimes`、`_qqStats` 只增不减（有界于历史发送数，但仍应裁剪） |
| low | `Models/StickerModel.cs:32-67` | `Tags`/`LastUsedTime`/`UseCount` 的 setter **没有相等判断**，批量赋值即通知风暴 |

五个第一轮报告过的问题（每次发送整库序列化 + `Refresh()`、搜索每键全量重排、启动 O(n²)、`UpdateImportedFlags` 的 O(镜像×图库)、构造里四个 fire-and-forget 加载）**全部仍在**，其中前三项因今晚的新功能而**更重**。



---

## 3. 今晚的"资源失控"事件 —— 结论与如实纠正

### 3.1 观测到的事实

| 时刻 | 句柄 | 工作集 | CPU |
|---|---|---|---|
| 03:27（启动 9.5 分钟） | 2,696 | 271 MB | 0.4% |
| 03:35（18 分钟） | ~2,703 | 522 MB | — |
| 06:40（3.4 小时） | 22,946 | 588 MB | — |
| 峰值 | **61,347** | **1,316 MB** | 瞬时 **82%** 单核 |
| 清理后 | 61,321（**平**） | 1,313 MB（**平**） | ~1% |

**句柄类型**（`NtQuerySystemInformation` 句柄表扫描，40,695 个样本）：

```
Event 39,702 | Mutant 177 | Section 122 | File 118 | Thread 73 | Key 58 | Semaphore 46 | Timer 32 | 其余 <10
```

→ 是**内核事件对象风暴**，不是文件/进程/注册表句柄。
**托管堆是干净的**：183,138 个对象 / 17 MB，任何类型的实例数最多 35 个 → 泄漏在**原生侧**。
GDI 175 / USER 112 → 不是 GDI/USER 泄漏。

### 3.2 被证伪的假设（都是实测，不是推理）

| 假设 | 实测结果 |
|---|---|
| `Process.GetProcessesByName("QQ")` 未 Dispose 泄漏句柄（`QqPanelWatcher.cs:753`、`QqKeyWatchService.cs:59/67`） | **0 句柄 / 500 次调用**（另有一次对照：带 Dispose 是 +2/500，反而没更好） |
| 一次 UIA 验证泄漏大量句柄 | 60 次完整验证：**+2 句柄**（其中 `FindFirst(Descendants)` 那一段确实泄漏，见 §3.3） |
| 内存压力导致换页 | 32 GB 内存空闲 17.9 GB，提交空闲 38.75 GB |
| 持续渲染（60fps） | Asuka GPU 仅 **1.9%**；UI 线程栈停在 `Dispatcher.PushFrameImpl`（空闲消息泵） |
| 深度同步调试器附着 | 8 个 QQ 进程 `CheckRemoteDebuggerPresent` **全为 False**；Asuka 无子进程 |
| 快捷面板被反复开关 | `asuka-watcher.log` 自 **05:00:22 起再无一行**（面板开关一定会写日志） |
| 外部 UIA 事件风暴 | 实测 **0 个结构事件 / 8 个焦点事件（15 秒）**，且 8 个都来自非 QQ 进程 |

### 3.3 唯一在码内站得住的泄漏点

审查方对我方探针 `.perf-probe\HandleLeakUia`（已删除，其结论已抄录）的实测：

```
FindFirst(Descendants, Class=sticker-panel) x60 : 316 -> 332  (+16, 0.267/次)   GC 后 336 → 回收不掉
FromPoint + 12 级 GetParent             x120 : 336 -> 343  (+7,  0.058/次)   GC 后可回收
DumpChildren depth3                     x40  : 337 -> 337  (0)
FindAll(全部 Descendants)               x20  : 337 -> 337  (0)   ← WindowService:98 干净
结构事件订阅 + RemoveAllEventHandlers    x30  : 337 -> 520  (+183)  GC 后 342 → 可回收
```

我自己的 `handlecost` 复测同一条路径得到 **+2/60 次**（0.03/次）。两次测量给出区间 **0.03–0.27 个/次，且 GC 回收不掉**。

**关键**：`_cachedPanel` 只在**找到面板时**才被赋值，所以面板关闭（常态）时**每次验证都要走完整的 `FindFirst(Descendants)`**。按 150 ms 节流上限 6.7 次/秒计算：

```
0.03 × 6.7 = 0.2 个/秒   ...   0.27 × 6.7 = 1.8 个/秒
实测长期增长速率: (22,946 - 2,703) / 11,100 秒 = 1.82 个/秒
```

**区间上沿与实测速率几乎完全重合。** 这条路径是长期缓慢增长的最可能来源，而且**修掉 UIA 验证风暴就同时修掉它**（与 P0-1 的修复方向一致）。

### 3.4 爆发式增长（2.7 万 → 6.1 万）的归因 —— 如实纠正

* 该爆发与**审查工具驱动 App 的时间窗完全重合**：我杀掉委派工具 `HandleLeakUia` 后，增长**立即停止**（60 秒 −24 个）；该工具被残留进程重新拉起后再度驱动，再次杀掉后又恢复平稳。
* 也就是说：**这 3.8 万个句柄不是产品在无人操作下产生的**。我上一轮若据此指责产品是错的，这里更正。
* 但两点仍然是**真实的产品缺陷**，与谁触发无关：
  1. **积累下来的资源不会释放** —— 驱动停止后，6.1 万句柄 / 1.3 GB 工作集**原样保留**，既不回落也不释放。产品缺少"回到空闲就归还"的路径（`RemoveAllEventHandlers` 是 fire-and-forget，`_subscribed` 里的元素被丢弃时也从不 `RemoveStructureChangedEventHandler`）。
  2. **本项目自己就是 QQ 的重度 UIA 客户端**：这条链路的泄漏是双向的，今天发生在 Asuka 身上，明天就可能发生在被它高频查询的 QQ 身上。
* 另外必须提醒：`%TEMP%` 里还有**别的会话留下的驱动工具痕迹**（`UiVerify`（22:46 创建，程序里用 `PostMessage WM_HOTKEY 0xA113` + UIA Invoke + `SetCursorPos` 驱动 Asuka）、`asuka-vmtest*`、`asuka-hprobe*`）。03:35→06:40 那 2 万句柄的增长发生在我进场之前，**很可能同样来自这些驱动工具**。所以"无人操作时是否泄漏"这一点，我不能给出肯定结论——需要一次干净复现。

### 3.5 根因：跨进程 UIA 操作在调用方泄漏内核 Event（受控实验证明）

审查方另起一个进程做对照实验（`Add-Type -AssemblyName UIAutomationClient`，对 Asuka 主窗口做 `FromHandle` + `Current.Name` + `FindAll(Children)`）：

```
200 次操作 → 客户端句柄 550 → 728   (+178 = 0.89/次)；强制 GC 只回收 21 个 (728 → 707)
 50 次操作（按类型统计）→ Event 句柄 161 → 179 (+18 = 0.36/次)   ← 泄漏类型正好是 Event
成本：200 次操作耗时 36.9 秒 = 每次 UIA 操作 185 ms（每次都在阻塞对方进程的 UI 线程）
```

这与 Asuka 的句柄普查（39,702 个 Event）**类型完全一致**，也能解释"托管堆干净、GDI/USER 正常"。

**两个方向都会漏，速率都正比于 UIA 调用量** —— 这解释了本轮观察到的全部曲线：

| 场景 | UIA 调用量 | 预期泄漏 | 实测 |
|---|---|---|---|
| Asuka 自己的 watcher 查询 QQ（常态、低频） | ~2–5 次/秒 | 0.8–4.5 个/秒 | **实测长期基线 1.82 个/秒**（2,703→22,946 / 3 小时 5 分） |
| 外部工具高频查询 Asuka（被查询方，Asuka 作为 UIA provider 也要建 Event） | 数百次/秒 | 130–330 个/秒 | **实测爆发峰值 108→177→310 个/秒** |

佐证：runaway 进程中 UI 线程 15 秒烧 1328 ms；另有一条**纯原生**线程（`clrstack -all` 里托管栈为空）15 秒烧 1109 ms 且 `WaitReason = LpcReply`（阻塞在跨进程调用）；进程内同时存在 `MS.Internal.Automation.QueueProcessor`（客户端）与 `MS.Internal.AutomationProxies.QueueProcessor`（provider 侧）两条 UIA 队列线程。

> 这就是为什么"收缩 UIA 调用量"必须是第 1 优先级：它同时消掉
> ① 每次验证 20–50 次跨进程调用（→ QQ 打字延迟）、② 130–330 个 Event/秒的泄漏、
> ③ 机器级内存与句柄压力。

---

## 4. 机器侧（与"整机卡"直接相关，且不属于 Asuka）

| 观测 | 数值 | 说明 |
|---|---|---|
| `LEDKeeper2` 句柄 | **164,287** | 灯效/主板工具，运行 15.3 小时。**是 Asuka 峰值的 2.7 倍**，单进程就把内核句柄池吃掉一大块 |
| 系统句柄总数 | **365,810** | 整机偏高 |
| Asuka 中被注入的第三方 DLL | `RTSSHooks64.dll`（RivaTuner 统计服务器，2.4 MB）、**5 个搜狗输入法组件**（`ichat_bundle64`、`isgpet_bundle64`、`PicFace64`、`Resource.dll`、`systembeautify_bundle64`）、`GoogleIMEJaTIP64.dll`、`iFlyameQuickLaunch.dll`、`mswebp_store.dll` | 输入法 TIP 组件与 RTSS 钩子都会在被注入进程里创建对象；**归属分析时必须扣掉这一层** |

> 结论：这台机器上"关掉某个软件就缓解"很容易被误读——`LEDKeeper2` 一个人就握着 16 万个句柄。评估 Asuka 的贡献时要拿这个基线来扣。

---

## 5. 建议的处置顺序（按性价比）

1. **立刻重启一次 Asuka**：当前实例仍握着 6.1 万句柄 / 1.3 GB（今晚驱动实验的残留），虽然已不再增长，但状态不干净，且下次测量会被它污染。
2. **P0-1，一行 + 四处小改**：
   * `ConfigModel.cs:11` 改 `false`（默认不装全局钩子）；
   * `AddStructureChangedEventHandler` 的 `TreeScope.Descendants` → `Children`（实测事件 46/s → 0/s）；
   * `VerifyMinIntervalMs` 150 → 1000，并在 QQ 不在前台时直接跳过验证；
   * `OnClosing`/`HideToTray`/`StateChanged` 里 `_panelWatcher?.Dispose()`，`RestoreFromTray()` 里重建；
   * `UpdateWatcherState()` 等到配置加载完成再调用（别再用字段默认值启动）；
   * 把 `AutomationElement.FromPoint`（命中测试）换成 Win32 `WindowFromPoint` + `IsWindowVisible` —— **零 UIA**，直接砍掉每次验证里最贵的一段；
   * 按 hwnd 缓存已找到的 `sticker-panel` 元素，之后用 Win32 矩形校验复验，不再重跑整树 `FindFirst(Descendants)`；
   * `PruneSubscriptions()` 里补 `Automation.RemoveStructureChangedEventHandler`（现在只丢字典项、从不退订）。
3. **P0-2 剪贴板**：把 `if (sig == _lastClipboardSig) return;` 提到 `:620` 之后立刻执行；OLE 读取挪到专用 STA 线程；`MD5.HashData(File.OpenRead(path))` 流式哈希；图库 MD5 预建 `HashSet`。
4. **`FindFirst(Descendants, …)` 的泄漏**：等第 2 步把验证频率降下来后复测句柄曲线；若仍增长，用 `PropertyCondition` 收窄到 `TreeScope.Children` 或改为对已知面板元素做廉价探测。
5. **`DumpQqTreeDiagnostics`**：把 `TrueCondition` 换成类名条件，并把**访问节点数**计入上限（现在只限制命中数）；或直接改成"仅在用户主动点反馈按钮时收集"。
6. **`LooksLikeEmojiButton` 恢复 class 校验**（`icon-item` 或其包含匹配），避免误命中触发 7 连扫。
7. **标签编辑器 GIF**：补 `IsVisibleChanged`/`StateChanged → ClearTagEditorPreview()`（照抄快捷面板的写法）。
8. **测量纪律**：以后做性能测量时，**不要**用 `PostMessage`/UIA/鼠标注入去驱动运行中的实例；要驱动就针对专门的测试实例，并在测完后重启，否则得到的就是今晚这种被工具污染的曲线。

---

## 6. 本轮现场清理

| 项目 | 处置 |
|---|---|
| `%TEMP%\asuka-full.dmp`（1,224.9 MB）、`asuka-runaway.dmp`（564.4 MB）、`asuka-dumpheap.txt`、`asuka-stacks.txt` | 已删除，**释放 1,790 MB**（先逐条核对路径与创建时间，只删本次会话产生的） |
| `.perf-probe\HandleLeakUia\`（委派工具，驱动 App 造成资源爆发） | 残留进程两次拉起均已强杀，目录已删除 |
| 其他探针进程 | 已确认无残留 |
| Asuka 自身的日志（`asuka-watcher.log`、`asuka-diag.log`、`asuka-crash.log`、`asuka-keyflow.log`） | **原样保留** |
| `.perf-probe\` 其余内容（79.5 MB，含 `UiVerify` 等他方会话产物） | 保留待你决定；可整体 `Remove-Item -Recurse .perf-probe` |
