# Asuka 1.3.5 批量管理

> 此页记录 1.3.5 的初版行为。1.3.6 已开放 QQ 页并将选择与标签页解耦，见 [1.3.6 说明](BATCH-MANAGEMENT-1.3.6.md)。

2026-10-23 本地发布，安装位置 `D:\Asuka\Asuka.exe`，另存一份发布文件到 `D:\Asuka\v1.3.5`。

## 操作

- 在图库右上角点击「批量管理」，点击图片切换选中状态，选中图片显示 4 DIP 主题色描边与对勾。
- 「全选 / 取消全选」只作用于当前搜索与标签页显示的图片，包含尚未滚动到的图片。支持 Ctrl+A，聚焦图片后可用空格切换选择；Esc 或「完成」退出。
- 切换标签页会清空选择，搜索后隐藏的图片会取消选择。QQ 收藏镜像页不提供图库批量操作；可先导入图库再管理。
- 「编辑标签」预填所有所选图片的共有标签，保存时只应用增删差量，各图片独有标签保留；取消不会写入修改。
- 「删除图片」确认所选数量后删除图库记录与对应文件。被占用的文件沿用现有待删除重试队列。
- 「AI 建议标签」逐张识别，最多同时 2 个请求，其余排队，成功结果按图片缓存。单张失败不影响其余结果，显示完成进度及部分失败数量。
- AI 结果合并为整批候选；用户点选并保存后，这些标签会应用到整批图片。关闭编辑器或切换编辑对象会取消正在执行与等待中的编辑器 AI 请求。
- AI 候选区支持滚动，选择标记不写入配置或图库索引。

## 验证

- Release 构建：0 警告、0 错误；`git diff --check` 通过。
- 批量专项：6 个模型用例及共享的真实隐藏 WPF 窗口用例通过。
- WPF 验证包括真实工具栏与图片点击路由、描边/对勾、标签保存/取消、临时图库中的确认/取消删除。
- 合成 HTTP 验证最高并发 2、缓存复用、单张 503 部分失败与关闭时取消队列；没有使用真实 API 额度。
- 临时把并发上限改为 3，测试准确报告 `batch AI made 6 requests at concurrency 3`；恢复为 2 后最终回归通过。
- 完整启用回归：212/212 通过。沿用已有 `--skip-key-bootstrap` 选择，排除 1 个历史上受本机安全软件影响的合成密钥引导用例。
- 保留工作区原有的 QQ 联动、无障碍恢复和快捷面板定位修改，并纳入本次构建及回归。

命令：

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\batch-management\tests\ -- --batch-only
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\batch-management\tests\ -- --skip-key-bootstrap
```

## 替换与核对

- 替换前备份：`D:\Asuka\backups\pre-batch-management-20261023-224736`，包含原程序、配置与图库索引等根目录文件，以及原图库图片校验清单。
- 首次替换遇到旧进程退出后的短暂文件占用，脚本已回滚；增加进程退出等待后重试成功。
- 发布的 5 个文件均与安装文件通过 SHA-256 对比。
- 程序版本 `1.3.5.0`，安装 EXE SHA-256：`4D0DCE17F28F5C75DA64AD152C42F05F6DED90F0B83C5BD0BC046910682F797E`。
- 新主进程 PID 47644；通过实际安装版窗口的 UI Automation 树确认「批量管理」入口可见。
- 140 张图库图片逐文件校验值保持一致；`config.json` 和 `stickers.json` 的 SHA-256 也保持一致。
- 部署详情保存在 `D:\Asuka\build-info.json`。

待用户测试：多选与全选的手感、标签编辑、删除提示，以及自有 AI 服务的实际识别结果。真实服务识别质量与计费未使用用户账号验证。
