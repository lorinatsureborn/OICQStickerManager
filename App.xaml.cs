using OICQStickerManager.Models;
using OICQStickerManager.Services;
using OICQStickerManager.ViewModels;
using OICQStickerManager.Views;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace OICQStickerManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // 全局共享 ViewModel：主窗口与快捷面板绑定同一实例（同一图库、同一过滤/排序视图状态）。
        // 懒构造：静态初始化器会在单实例互斥判定之前把整个 VM（图库装载+QQ 扫描）跑完，
        // 第二实例白白做全套 IO 才退出；且 VM 构造会绑定当前 Dispatcher，首次取用必须在 UI 线程
        private static readonly Lazy<MainViewModel> _sharedViewModel = new(() => new MainViewModel());
        public static MainViewModel SharedViewModel => _sharedViewModel.Value;

        // 是否为主实例：决定 OnExit 是否要冲刷保存（第二实例不碰数据文件）
        private static bool _isPrimaryInstance;

        // 单实例互斥（强制行为，非设置项）：双开会让热键注册冲突、QQ watcher 双份。
        // 后启动者只负责唤醒已有实例（RestoreFromTray）然后退出。
        private const string MutexName = @"Local\Asuka.SingleInstance";
        private const string ActivateEventName = @"Local\Asuka.Activate";
        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _activateEvent;

        // 静默启动：必须在创建主窗口前知道（决定 Show/Hide 时序），所以不等 VM 异步加载，
        // 启动时直接快照读一次 config.json
        public static bool SilentStartRequested { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 诊断自检：--decrypt-selftest <emoji.db路径> <密钥> → C# SQLCipher 解密验证（Phase B）
            if (e.Args.Length == 3 && e.Args[0] == "--decrypt-selftest")
            {
                RunDecryptSelfTest(e.Args[1], e.Args[2]);
                Shutdown(0);
                return;
            }

            // 诊断自检：--extract-key → 跑官方脚本提取密钥并落结果文件（Phase B 验证）
            if (e.Args.Length == 1 && e.Args[0] == "--extract-key")
            {
                RunExtractKeySelfTest();
                Shutdown(0);
                return;
            }

            // 主窗口渲染前先落定配色，避免以错误主题闪一帧
            ThemeManager.ApplyInitial();
            // 用户自定义的玻璃材质浓度在主题切换后自动重涂
            GlassMaterial.EnsureHooked();

            _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                {
                    using (existing) existing.Set();
                }
                Shutdown();
                return;
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _isPrimaryInstance = true;
            // 后台线程等唤醒信号：第二实例启动时把托盘里/后台的主窗口带回前台
            var wakeListener = new Thread(() =>
            {
                try
                {
                    while (_activateEvent.WaitOne())
                        Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RestoreFromTray());
                }
                catch { /* 退出时句柄被释放，随进程结束 */ }
            })
            { IsBackground = true };
            wakeListener.Start();

            SilentStartRequested = ReadSilentStartFlag();

            // 全局兜底：未处理异常（包括 async void 命令/事件处理器抛出的）只弹窗提示，不闪退。
            // TargetInvocationException 等包装异常只显示外壳毫无线索，遍历内层并落盘完整栈。
            DispatcherUnhandledException += (_, args) =>
            {
                var messages = new System.Text.StringBuilder();
                for (var ex = (Exception?)args.Exception; ex != null; ex = ex.InnerException)
                    messages.AppendLine(ex.Message);
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asuka-crash.log"),
                        $"———— {DateTime.Now:HH:mm:ss} ————\n{args.Exception}\n\n");
                }
                catch { /* 日志失败不影响兜底 */ }
                MessageBox.Show($"程序遇到未处理的异常：\n{messages}", "飞鸟",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // 主窗口手动创建（StartupUri 已移除）：静默启动时 Show 后立刻 Hide——
            // Loaded/快捷面板预热/热键注册照常执行，同一次调度内屏幕上不留帧
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            if (SilentStartRequested) window.Hide();
        }

        private static bool ReadSilentStartFlag()
        {
            try
            {
                var path = MainViewModel.GetConfigPath();
                if (!File.Exists(path)) return false;
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                return config?.SilentStart == true;
            }
            catch { return false; }
        }

        // --decrypt-selftest：解密 emoji.db 副本并落盘明文库，输出首 16 字节作校验
        private static void RunDecryptSelfTest(string dbPath, string key)
        {
            try
            {
                var encrypted = File.ReadAllBytes(dbPath);
                var plain = SqlcipherDecryptor.Decrypt(encrypted, key);
                var outPath = Path.Combine(Path.GetTempPath(), "asuka-decrypt-out.db");
                File.WriteAllBytes(outPath, plain);
                var head = System.Text.Encoding.ASCII.GetString(plain, 0, 15);
                Console.WriteLine($"decrypted {plain.Length} bytes, head={head}, saved={outPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELFTEST FAILED: " + ex.Message);
                Environment.ExitCode = 1;
            }
        }

        private static void RunExtractKeySelfTest()
        {
            try
            {
                var key = Services.QqKeyExtractor.RunScriptAndExtractAsync().GetAwaiter().GetResult();
                var line = key == null ? "EXTRACT FAILED (null)" : $"EXTRACTED: {key}";
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "asuka-extract-out.txt"), line);
                if (key == null) Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "asuka-extract-out.txt"),
                    "EXTRACT EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                Environment.ExitCode = 1;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 冲刷退出瞬间仍在途的配置保存（快速开关应用时最后一步不落地会导致
            // 绑定/设置回滚——2026-10-01 用户实测），最多等 2 秒；第二实例没建过 VM，别把它拉出来
            if (_isPrimaryInstance) SharedViewModel.FlushPendingConfigSave(2000);
            _activateEvent?.Dispose();
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
