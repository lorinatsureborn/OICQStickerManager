# Windows 10 / QQ 9.9.36 上屏延迟修正

日期：2026-10-07。Asuka 1.3.4。

## 原因

1.3.3 的共存发送在没有可用输入框缓存时，用一次性 `Asuka.exe --qq-uia-query editor` 查找输入框。点击后缺少新的编辑焦点事件时，还用一次性进程查询 `focus`。这些进程隔离了可能卡住的 QQ UIA 调用，但每次都需启动 .NET/WPF，造成可避免的延迟。

实际 QQ 为 `9.9.36-53644`，系统为 Windows 10 19045。本机只读实测：

| 查询方式 | 输入框定位 | 焦点查询 |
| --- | --- | --- |
| 一次性进程，两次采样 | 297 / 261 ms | 222 / 216 ms |
| 复用常驻进程，两次热态采样 | 15 / 15 ms | 2 / 0 ms |

为测量启动边界，额外新建了一次常驻辅助进程，其第一条输入框请求用了 647 ms，包含 watcher 初始化及订阅等待。表中热态数字不能理解为冷启动承诺或完整上屏时间。测量没有点击输入框、写入剪贴板或发送聊天内容。

## 修改

- 共存监听已经有常驻的 UIA 隔离进程，输入框与焦点查询复用该进程，通过请求编号返回结果。
- 查询超时仍会回收并重启对应进程；超时后不会另起一次性查询绕过隔离保护。没有运行共存监听时，诊断查询仍可使用一次性进程。
- 保留目标窗口、用户操作代数、鼠标操作与发送前编辑焦点校验，也保留现有的 IME 安全点击路径。
- 保留点击后的 100 ms 稳定观察。连续发送也仍会等待上一张最多约 1 s 的剪贴板读取保护；它可延迟下一张，但不是第一张上屏前的固定等待。该间隔没有 QQ 已完成读取的可靠回执，本轮未缩短。

## 验证与发布

- 新增两个回归先在原实现失败，修正后通过：发送查询复用存活进程；发送查询超时回收且不另开查询。
- 隔离进程回归：7/7 通过，包括挂起回收、退出清理、请求关联和状态发布顺序。
- 整体启用回归：191/191 通过，仍使用 `--skip-key-bootstrap` 排除上一轮已记录的合成密钥助手夹具。本次没有修复或重新认定该夹具。
- Release 构建及 win-x64 单文件发布成功。部署文件逐项 SHA-256 校验一致。
- 对部署后的单文件程序再作只读辅助进程检查：同一进程连续返回输入框、焦点查询；热态输入框 17 / 22 ms、焦点 2 / 1 ms，首次请求含初始化为 496 ms。关闭其标准输入后正常退出，退出码 0；没有停止用户正在运行的 1.3.3。
- 发布至 `D:\Asuka\v1.3.4`。程序沿用“文档/OICQStickerManager”内的现有数据；从托盘退出当前旧版后运行该目录的 `Asuka.exe`。旧版运行中打开新版会因单实例机制唤醒旧版。
- 真实上屏体验仍由用户实机复测，本轮只读测量证明查询耗时下降，不等同于端到端上屏测量。

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\win10-performance\tests\ -- --uia-only
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\win10-performance\tests\ -- --skip-key-bootstrap
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\win10-performance\tests\ -- --benchmark-qq-queries <QQ窗口句柄>
```
