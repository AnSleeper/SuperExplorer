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
                    var p = Path.GetFullPath(e.Args[0]);
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
            if (SettingsService.FreshInstall)
            {
                // 全新安装：默认把当前用户桌面加入常用文件夹
                try
                {
                    var desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (!string.IsNullOrEmpty(desk) && Directory.Exists(desk))
                        SettingsService.Current.Favorites.Add(desk);
                }
                catch { }
            }

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
