# Windows 10 / QQ 9.9.36 本地适配与发布记录

日期：2026-10-07。发布版本：Asuka 1.3.3。

## 合流范围

NAS 远端 `nas-git:lori/OICQStickerManager.git` 的 `main` 已拉取至 `8e4cc776d96d2edbb3356f7b35be0039fbb6c69c`。
合流保留 NAS 的 UIA 辅助进程、原生面板生命周期监听、输入焦点/剪贴板事务、AI 配置安全和其他回归修复。
本地 `42a4415` 与 NAS 在 watcher / 主窗口两处冲突，按隔离进程结构合流；其面板内关闭盲区改由原生点击触发可见性复核，失焦/隐藏/最小化由原生生命周期监听收起。

## 本机发现与处理

- 系统为 Windows 10 19045，.NET SDK 10.0.401 / Desktop Runtime 10.0.12。
- `F:\SoftWare\QQ\QQ.exe` 的启动器文件版本仍是 `9.9.19.34740`，实际应用模块是 `versions\9.9.36-53644\resources\app\wrapper.node`，`versions\config.json` 的 `curVersion` 也为 `9.9.36-53644`。
- 原 NAS 代码会据启动器版本将本机新版 QQ 错判为旧版。现在优先读取进程已加载模块的版本，无模块的子进程读取启动器的实际活动模块，最后才回退文件版本；不会按目录最大版本猜测，也不会把待重启的升级配置覆盖到仍在运行的旧模块。
- 版本推断与实际 UIA 不可用证据分开记录；修正版本信息可以清除版本误判，实际慢树降级仍只作用于相应进程，进程重启后复位。
- 新版 QQ 首次打开使用实际探测的面板边界；没有有效边界时等真实出现事件。旧版才使用原有 DPI 感知的工具栏几何回退，避免把新版面板放在旧版坐标上或把光标小矩形当面板。
- 点击 QQ 面板内部的 X、表情或分类会触发短时可见性复核，处理透明隐藏、节点复用和结构事件遗漏。实际仍可见时继续保持共存；过期会话的排队复核不会操作新会话。
- 编辑器定位完成后、每次恢复点击前后均复核发送是否仍属于当前操作；用户切走或开始其他操作后取消恢复点击。
- 粘贴前与剪贴板重试时，新旧版均检查当前编辑焦点证据。新版可接受直接 UIA 确认，但其后的非编辑焦点变化会取消粘贴。
- 沿用原文件 FileDropList + Ctrl+V；不按 Enter，不用 UIA SetFocus，不增加粘贴后的延迟 toggle。

## 验证

- 新增 11 项回归均先失败再修正：5 项版本/降级、2 项原生面板内部点击、2 项面板锚、2 项焦点恢复与粘贴闸门。
- QQ 核心回归：45/45 通过。
- 启用的整体回归：189/189 通过，命令附带 `--skip-key-bootstrap`。
- 未过滤的早期整体运行：187/188 通过，唯一失败为 `Key helper bootstrap bypasses registry detection and quotes paths safely`，助手未输出预期合成结果。该夹具在 NAS 的先前审计中也被单列排除。本轮没有修改安全策略或声称该用例已修复；没有读取真实 QQ 密钥。
- 实际运行进程诊断：本机 QQ 进程全部报告 `9.9.36-53644, legacy=False`。
- Release 构建与 win-x64 单文件发布成功。
- 部署文件与构建暂存文件逐项 SHA-256 一致。
- 使用独立 `ASUKA_DATA_DIR` 启动部署后的 `D:\Asuka\Asuka.exe`：版本 1.3.3.0，主窗口响应正常，UIA 辅助进程成功启动，日志识别 Windows 10 / QQ 9.9.36。正常关闭主窗口后，主进程与辅助进程均退出；没有向真实 QQ 草稿或聊天发送图片。

## 发布与实机测试边界

发布目录：`D:\Asuka`。替换前备份：`D:\Asuka\backups\v1.3.2-20261007-032640`，包含原程序文件与已落盘的配置/图库索引等元数据。
旧版实例不响应自身唤醒事件，备份后已停止该实例。新程序仅用隔离测试配置做启动/退出检查，实际用户配置由用户下一次正常启动时加载。

Windows 窗口捕获对当前 QQ 连续两次超时，已停止界面自动化。上述回归及启动退出检查不能代替以下实机体验：

1. 冷启动首次打开、连续开关和双击表情按钮，两个面板是否正确联动。
2. QQ 面板内关闭 X、点原生表情、切分类，以及点击消息区/输入区，是否及时收起且不误重弹。
3. 切到其他应用、最小化/恢复 QQ、关闭聊天窗口，是否无残留面板。
4. 静态图片与 GIF 是否真正插入输入框；发送期间切窗或改变焦点是否取消粘贴。
5. 插入表情后继续中文输入，输入法候选框是否仍跟随光标。
6. 从托盘退出飞鸟后是否立即释放进程，随后能否正常重新启动。

### 复核命令

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\win10-audit\tests\ -- --qq-core-only
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\win10-audit\tests\ -- --skip-key-bootstrap
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\win10-audit\tests\ -- --inspect-qq-builds
dotnet publish OICQStickerManager.csproj -c Release -p:PublishProfile=FolderProfile
```
