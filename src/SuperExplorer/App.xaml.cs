using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SuperExplorer
{
    public partial class App : Application
    {
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
