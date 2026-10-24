using System;
using Microsoft.Win32;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 把「双击文件夹」接管到本程序：向 HKCU 写入 shell\open\command 覆盖项。
    /// HKCU 键会遮蔽系统默认值，删除即还原；只删除指向本程序 exe 的键，不碰其他软件的设置。
    /// 接管后双击文件夹会以路径为参数启动本程序，由单实例转发到已运行的窗口。
    /// </summary>
    public static class ExplorerTakeoverService
    {
        private static readonly string[] Roots =
        {
            @"Software\Classes\Directory\shell\open",
            @"Software\Classes\Drive\shell\open",
            @"Software\Classes\Folder\shell\open"
        };

        private static string ExePath
        {
            get
            {
                try { return Environment.ProcessPath; }
                catch { return null; }
            }
        }

        public static void Apply(bool enable)
        {
            var exe = ExePath;
            if (string.IsNullOrEmpty(exe)) return;

            foreach (var root in Roots)
            {
                try
                {
                    if (enable)
                    {
                        using (var key = Registry.CurrentUser.CreateSubKey(root + @"\command"))
                        {
                            key.SetValue(null, "\"" + exe + "\" \"%1\"");
                        }
                    }
                    else
                    {
                        // 只删除指向本程序的接管，避免误删其他软件的同类设置
                        string cmd = null;
                        using (var key = Registry.CurrentUser.OpenSubKey(root + @"\command"))
                        {
                            if (key != null) cmd = key.GetValue(null) as string;
                        }
                        if (cmd != null && cmd.IndexOf("SuperExplorer", StringComparison.OrdinalIgnoreCase) >= 0)
                            Registry.CurrentUser.DeleteSubKeyTree(root);
                    }
                }
                catch { }
            }
        }

        public static bool IsRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Roots[0] + @"\command"))
                {
                    var cmd = key == null ? null : key.GetValue(null) as string;
                    return cmd != null && cmd.IndexOf("SuperExplorer", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }
    }
}
