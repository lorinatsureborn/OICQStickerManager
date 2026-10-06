# AI 标签推荐与 API 接入核查

核查日期：2026-10-07。初次核查基于本地 `9758184` 源码；推送前已与后续 QQ 表情联动及上屏修复一起完成独立快照验证。

## 已确认并修复

| 问题 | 影响 | 修复 |
| --- | --- | --- |
| OpenAI 专用前缀未参与自动识别 | `sk-proj-`、`sk-svcacct-` 要求手动选厂商 | 识别这两类前缀；通用 `sk-` 仍需手动选择，避免错误猜测 |
| OpenAI 请求固定使用 `max_tokens` | 部分推理模型拒绝请求；模型探测也会漏掉这些模型 | OpenAI 标注与探测统一使用 `max_completion_tokens` |
| Claude 预设包含退役模型 | Opus 4.1、Haiku 3.5 调用会失败 | 移除退役预设，加入当前 Haiku、Sonnet、Opus 模型；OpenAI 预设也补充当前视觉模型 |
| 模型查询套用智谱分页参数且只读一页 | OpenAI 收到无关参数；Claude 后续页模型遗漏 | 各厂商分别构造查询；Claude 使用 `limit`、`after_id`、`has_more`、`last_id` |
| 忽略 Claude 图片能力元数据 | 不必要的逐模型付费探测 | 读取 `capabilities.image_input.supported`，已有明确能力声明时直接筛选 |
| 模型列表错误统一报 404 | 限流、服务异常被误诊为接口不存在 | 保留实际 HTTP 状态及可执行的错误提示；补过滤图片生成和 realtime 模型 |
| 选模型未更新激活档案 | 重启或切换档案后退回旧模型 | 模型选择同步写回档案和状态显示 |
| 改 Key、服务商、地址继承旧验证状态 | 未测试的新配置可以保存；跨厂商沿用旧模型 | 重置验证状态；自动识别过的 Key 被替换后重新识别；换厂商清理模型和强度；自定义地址变化脱离旧档案 |
| 模型输入藏在验证成功之后 | 自定义接口无法填写测试需要的模型；受限 Key 无法选择有权限的模型 | 验证前允许选模型、输入模型名、检测模型；验证通过后允许保存 |
| 配置变化未刷新动态控件 | 粘贴 Key 后厂商/模型界面不同步 | 合并调度设置界面刷新；增加更换服务商入口；测试前提交输入绑定 |
| 异步结果回填到新的 Key、档案或图片 | 旧 Key 验证了新 Key；上一张图的标签出现在另一张图编辑器 | 按配置版本和编辑会话核对回填目标；已过期结果不再更新当前界面 |
| 取消被包装为识别失败、空内容未按承诺重试 | 取消语义错误；偶发空输出直接失败 | 保留取消异常；空输出与非 JSON 输出统一最多重试一次 |
| 日志仅按空格分词脱敏 | JSON 引号/换行内的 Key 可能漏出；Bearer 后的任意令牌未屏蔽 | 使用正则屏蔽常见 Key 和 Bearer 令牌；错误提示中的 Key 也脱敏 |
| 缓存忽略独立数据目录且 Flush 不等待已排队写入 | 隔离实例写进默认缓存；退出可能丢掉最近的识别结果 | 使用应用数据根目录，捕获每次写入路径，按序写入，退出等待排队任务 |

## OpenAI 与 Claude 协议结论

- OpenAI：`Authorization: Bearer <key>`、`POST /v1/chat/completions` 和 `GET /v1/models`。图片使用 `image_url.url` 中的 Base64 data URL。[模型列表接口](https://developers.openai.com/api/reference/resources/models/methods/list)、[Chat Completions 参数](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)、[图片输入](https://developers.openai.com/api/docs/guides/images-vision)。
- Claude：官方确实提供 OpenAI 兼容层，原来的 Chat Completions/Bearer 接入可用于本功能。模型查询采用官方文档中的 `x-api-key` 与 `anthropic-version: 2023-06-01`，标注继续使用兼容接口。兼容层会忽略 `reasoning_effort`，因此本项目没有为 Claude 显示无效的强度控件。[兼容层文档](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk)、[模型查询与分页](https://platform.claude.com/docs/en/api/models/list)。
- 旧的 Claude Opus 4.1、Haiku 3.5 已退役，Sonnet 4.5 已弃用。新预设以核查日的官方模型与生命周期页面为依据。[Claude 模型与生命周期](https://platform.claude.com/docs/en/about-claude/model-deprecations)、[OpenAI 模型目录](https://developers.openai.com/api/docs/models/all)。
- 预设表示已知支持视觉的候选；实际可用性仍受 Key 的账号、模型权限和额度影响。模型检测使用能力元数据；无元数据的候选会发送小图，按服务商计费。

## 验证范围与本机测试

新增 35 项 AI 回归测试，包含模拟 HTTP 接口、真实 PNG 解码、真实加密档案保存/重载、缓存落盘，以及不显示的 WPF 窗口。模型认证、请求体、分页、界面异步回填、解密失败、旧明文字段拒绝、写入中断留下的临时配置、密码框和错误脱敏均有覆盖。测试使用合成 Key，不调用真实厂商接口。

本次推送前从 Git 暂存区独立导出提交内容，Release 编译为 0 警告、0 错误；包含上述 35 项 AI 测试及 QQ 回归在内的 178/178 启用用例全部通过。运行参数为 `--skip-key-bootstrap`，仅排除此前被本机安全软件阻止的合成 PowerShell 密钥助手启动用例，没有改变安全配置。

供本机测试的程序：`bin/ai-secure/app/Asuka.exe`，版本标识仍为 1.3.2，本次从修复后的工作区重新构建。使用通常的用户数据目录。

建议本机依次测试：粘贴 OpenAI/Claude Key 自动识别；测试连接；检测账号可用模型；保存档案并切换；改变模型后重启；单张与批量标签推荐；识别中关闭编辑器再打开另一张图。

真实账号权限、代理网络、额度及识别质量需通过本机 Key 实测。Claude 对可访问多个 workspace 的 personal/service-account Key 可能要求 `anthropic-workspace-id`；当前界面没有 workspace 参数入口，测试时可使用绑定单一 workspace 的 Key。[官方认证与 workspace 说明](https://platform.claude.com/docs/en/api/overview)。

## 密钥存储改进

AI API Key 复用 QQ 数据库密钥已有的 Windows DPAPI `CurrentUser` 机制。档案只保存 `ProtectedApiKey` 密文；顶层 `AiTagApiKey` 和档案的明文 `ApiKey` 字段已移除。编辑中的草稿 Key 仅保存在内存中，测试通过后命名保存才加密进入档案。新配置、备份和写入中断留下的 `.tmp` 均经过同一保护边界。

AI 功能尚未发布，不读、不迁移旧 AI 明文字段；备份时丢弃这些字段。QQ 已发布配置的读取规则保持现状。解密失败不会丢弃整份配置或覆盖无法解锁的密文：该档案不再视为已验证，设置页提示重新输入并测试 Key。

输入控件使用 `PasswordBox`，档案行不展示 Key 片段。响应、异常和日志先屏蔽本次请求的完整 Key 及其 JSON/URL 转义形式，再屏蔽常见令牌格式，覆盖没有固定前缀的中转 Key。请求配置对象的 `ToString()` 也不输出 Key；向服务商发送请求时仍使用内存中的原始 Key。

DPAPI 保护绑定当前 Windows 用户，通常也绑定本机。换电脑、换 Windows 用户或系统环境无法解密时需要重新输入 Key；它主要防止配置文件或备份被复制后直接泄露明文，不能阻止在同一用户权限下运行的程序访问密钥。[Microsoft DPAPI 文档](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)。
