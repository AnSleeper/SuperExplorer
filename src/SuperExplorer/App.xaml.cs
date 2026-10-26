using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SuperExplorer.Services;

namespace SuperExplorer
{
    public partial class App : Application
    {
        private const string MutexName = "SuperExplorer.SingleInstance";
        private const string PipeName = "SuperExplorer.Pipe";

        private Mutex _mutex;
        private MainWindow _main;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // WPF 在退出（含关机 WM_QUERYENDSESSION）时会初始化控件遥测，
            // 按需加载 System.Diagnostics.Tracing；单文件发布下这一步偶发
            // FileNotFoundException。启动时预加载一次，退出阶段直接复用，
            // 不再触发文件探测。
            try { _ = typeof(System.Diagnostics.Tracing.EventSource).Assembly; }
            catch { }

            // 同类问题：Popup / ToolTip 首次弹出时 WPF 会走
            // PopupSecurityHelper.ForceMsaaToUiaBridge，按需加载 Accessibility 程序集，
            // 单文件发布下同样偶发 FileNotFoundException（曾进 error.log 并弹错误框）。
            // 程序集本身在包内，只是解析时机不对；启动时先取一次让它常驻，后续弹窗直接命中。
            try { _ = typeof(Accessibility.IAccessible).Assembly; }
            catch { }

            DispatcherUnhandledException += OnUnhandled;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                WriteCrash(args.ExceptionObject as Exception);
            };

            // 命令行参数是一个存在的文件夹时视为「打开请求」（接管模式下由系统转发）
            string argFolder = null;
            if (e.Args != null && e.Args.Length > 0)
            {
                try
                {
                    // 盘符根目录（C:\）经 Shell 引号包裹后，尾反斜杠会把引号转义，
                    // 收到的参数形如 C:" —— 剥掉残缺的尾引号再解析
                    var raw = e.Args[0].Trim().TrimEnd('"');
                    var p = Path.GetFullPath(raw);
                    if (Directory.Exists(p)) argFolder = p;
                }
                catch { }
            }

            // 单实例：已有实例在运行时，把打开请求转发过去，自己退出
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                SendToRunningInstance(argFolder);
                Shutdown();
                return;
            }

            SettingsService.Load();

            // 每个新版本的第一次运行都把桌面补进常用文件夹（用户手动取消后本版本不再重复添加），
            // 这样覆盖升级新版本的用户也能拿到默认常用项
            try
            {
                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
                if (ver.Length > 0 &&
                    !string.Equals(SettingsService.Current.SeededFavoriteVersion, ver, StringComparison.OrdinalIgnoreCase))
                {
                    var desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (!string.IsNullOrEmpty(desk) && Directory.Exists(desk))
                    {
                        var favs = SettingsService.Current.Favorites;
                        bool exists = false;
                        foreach (var f in favs)
                        {
                            if (string.Equals(f, desk, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                        }
                        if (!exists) favs.Add(desk);
                    }
                    SettingsService.Current.SeededFavoriteVersion = ver;
                    SettingsService.Save();
                }
            }
            catch { }

            ThemeService.Apply(SettingsService.Current.Theme);
            UiScaleService.Apply(SettingsService.Current.FontStep);

            _main = new MainWindow(argFolder);
            _main.Show();

            StartPipeServer();
        }

        private static void SendToRunningInstance(string folder)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(2000);
                    var msg = string.IsNullOrEmpty(folder) ? "activate" : "open\t" + folder;
                    var bytes = Encoding.UTF8.GetBytes(msg + "\n");
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                }
            }
            catch { }
        }

        // 常驻管道服务：接收第二个实例转来的「打开文件夹 / 激活窗口」请求
        private void StartPipeServer()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (var sr = new StreamReader(server, Encoding.UTF8))
                            {
                                var msg = sr.ReadLine();
                                if (!string.IsNullOrEmpty(msg))
                                {
                                    var folder = msg.StartsWith("open\t", StringComparison.Ordinal)
                                        ? msg.Substring(5) : null;
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        if (_main != null) _main.ActivateFromOutside(folder);
                                    }));
                                }
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrash(e.Exception);
            e.Handled = true;
            MessageBox.Show("发生了一个错误：\n" + e.Exception.Message, "超级资源管理器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static void WriteCrash(Exception ex)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }
}
