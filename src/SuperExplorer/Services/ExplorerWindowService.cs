using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 枚举 / 关闭当前打开的资源管理器文件夹窗口（Shell COM）。
    /// 用于启动时把已打开的资源管理器窗口「接管」进本程序。
    /// </summary>
    public static class ExplorerWindowService
    {
        public static List<string> GetOpenFolderPaths()
        {
            var list = new List<string>();
            object shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return list;
                shell = Activator.CreateInstance(t);

                object wins = Invoke(shell, "Windows");
                if (wins == null) return list;

                int count = (int)Get(wins, "Count");
                for (int i = 0; i < count; i++)
                {
                    object w = Invoke(wins, "Item", i);
                    if (w == null) continue;

                    string fullName = Get(w, "FullName") as string;
                    if (fullName == null ||
                        !fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                    var path = UrlToPath(Get(w, "LocationURL") as string);
                    if (path != null) list.Add(path);
                }
            }
            catch { }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }
            return list;
        }

        public static void CloseFolderWindows(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            object shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return;
                shell = Activator.CreateInstance(t);

                object wins = Invoke(shell, "Windows");
                if (wins == null) return;

                var toClose = new List<object>();
                int count = (int)Get(wins, "Count");
                for (int i = 0; i < count; i++)
                {
                    object w = Invoke(wins, "Item", i);
                    if (w == null) continue;

                    string fullName = Get(w, "FullName") as string;
                    if (fullName == null ||
                        !fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                    var path = UrlToPath(Get(w, "LocationURL") as string);
                    if (path == null) continue;
                    foreach (var p in paths)
                    {
                        if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) { toClose.Add(w); break; }
                    }
                }

                foreach (var w in toClose)
                {
                    try { Invoke(w, "Quit"); } catch { }
                }
            }
            catch { }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }
        }

        private static string UrlToPath(string url)
        {
            if (string.IsNullOrEmpty(url) ||
                !url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var path = new Uri(url).LocalPath;
                return Directory.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().InvokeMember(method,
                System.Reflection.BindingFlags.InvokeMethod, null, target, args);
        }

        private static object Get(object target, string property)
        {
            return target.GetType().InvokeMember(property,
                System.Reflection.BindingFlags.GetProperty, null, target, null);
        }
    }
}
