namespace OICQStickerManager.Models;

public class AppConfig
{
    public SendMode CurrentSendMode { get; set; } = SendMode.DoubleClick;

    // 发送后是否恢复剪贴板原内容；关闭则表情保留在剪贴板，便于连续粘贴
    public bool RestoreClipboardAfterSend { get; set; } = true;

    // QQ 表情面板共存触发器：点开 QQ 表情面板时自动在旁边弹出快捷面板
    public bool EnableQqCoexistTrigger { get; set; } = true;

    // 共存触发器的轮询保底：事件失灵（开 QQ 面板后我们的面板不弹/不收）时开启
    public bool EnableWatcherPolling { get; set; } = false;

    // 快捷面板热键：Win32 MOD_* 标志（1=Alt 2=Ctrl 4=Shift 8=Win）与虚拟键码，默认 Ctrl+Alt+D
    // （历史默认变迁 E→K→D；按配置原样加载，用户设置的任何键位一律尊重，无迁移）
    public int HotkeyModifiers { get; set; } = 3;
    public int HotkeyKey { get; set; } = 0x44;

    // 界面配色主题，对应 ThemeManager.Themes 中的 Id（aurora / mint / midnight / ember）
    public string ThemeId { get; set; } = "aurora";

    // 玻璃材质不透明度（百分比 50-100）：雾面的白度与不透明程度，设置页实时调整
    public int GlassOpacity { get; set; } = 70;

    // 主窗口 ✕ 的行为：true=隐藏到托盘驻留（从托盘菜单退出），false=直接退出程序
    public bool CloseToTray { get; set; } = false;

    // 用户是否已对关闭行为做出过选择（首次点 ✕ 时弹一次询问并记住；Esc/空白关掉弹窗不算选择）
    public bool CloseBehaviorDecided { get; set; } = false;

    // 启动后不显示主窗口，直接驻留托盘（为开机自启动准备的安静模式；开启时托盘图标常驻）
    public bool SilentStart { get; set; } = false;

    // 图库悬浮 400ms 的 GIF 动图预览气泡（默认开）
    public bool EnableGifHoverPreview { get; set; } = true;

    // QQ 账号绑定（一对多）：用户显式绑定后才出现 QQ（账号）选项卡；软件绝不主动绑定
    public List<QqBindingInfo> QqBindings { get; set; } = new();

    // 启动询问中被用户点过"不再询问"的账号，永不再问
    public List<string> QqPromptDismissedUins { get; set; } = new();

    // WebP 支持警告被用户处理过（点了"知道了"或"打开安装页"），不再重复弹
    public bool WebpNoticeDismissed { get; set; }

    // 复制图片后弹轻提示一键入库（M2 剪贴板捕获，默认开）
    public bool CaptureClipboardImages { get; set; } = true;

    // 入库轻提示的停留时长（秒，3-15）：超时未处理视为忽略；默认 8 秒
    public int ToastDurationSeconds { get; set; } = 8;

    // QQ 里点「添加到表情」后自动复制进图库（M4 联动，默认关）
    public bool QqFavoriteAutoImport { get; set; } = false;

    // QQ 收藏深度同步总开关（Phase B：解密 emoji.db 对账孤儿，默认关）
    public bool QqDeepSyncEnabled { get; set; } = false;

    // 孤儿处理策略：0=仅标记 1=同步删除 2=挪入图库
    public int QqSyncStrategy { get; set; } = 0;

    // emoji.db 密钥（16 字符，官方脚本提取；QQ 大版本更新后可能失效需重新引导）
    public string QqDbKey { get; set; } = "";

    // 排序模式（图库 / 快捷面板各自独立）：0=前 N 张最近置顶+其余热度，1=全部热度，2=全部按名称
    public int GallerySortMode { get; set; } = 0;
    public int QuickPanelSortMode { get; set; } = 0;

    // "最近置顶"的张数（1-20，默认 5）：最近发送的图会高频连发，置顶便于快速再找
    public int RecentPinnedCount { get; set; } = 5;

    // ———— AI 视觉标签建议（用户自备 API Key，见设置 → AI 识别）————
    // Key 明文存本机 config.json（与 QqDbKey 同口径），不上传任何服务器
    public string AiTagApiKey { get; set; } = "";

    // 服务商 id：auto=按 Key 前缀自动识别；其余见 AiTagService.Providers
    public string AiTagProvider { get; set; } = "auto";

    // 模型名：空=用服务商默认视觉模型
    public string AiTagModel { get; set; } = "";

    // 自定义服务商的接口地址（OpenAI 兼容 /chat/completions 根，如 https://xxx/v1）
    public string AiTagBaseUrl { get; set; } = "";

}
