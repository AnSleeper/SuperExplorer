using System;
using System.IO;
using System.Text;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 临时诊断日志：拖放换位这种「有时行有时不行」的问题光读代码定不了论，
    /// 先把每个环节落到 logs/drag.log，定位完可以整块删掉（不影响其他功能）。
    /// </summary>
    public static class DebugLog
    {
        private static readonly object _lock = new object();

        private static readonly string LogPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "logs", "drag.log");

        public static void Write(string tag, string message)
        {
            try
            {
                lock (_lock)
                {
                    var dir = System.IO.Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // 只看最近一次复现，超过 256KB 直接重来，别把用户磁盘写满
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();

                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + tag + "  " + message + "\r\n",
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
