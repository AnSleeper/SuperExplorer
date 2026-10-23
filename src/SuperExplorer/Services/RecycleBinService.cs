using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 通过 Shell COM 在回收站里找到原始路径对应的条目并执行「还原」。
    /// 不用 SHFileOperation，因为回收站条目没有可直接移动的稳定路径。
    /// </summary>
    public static class RecycleBinService
    {
        public static bool Restore(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return false;

            object shell = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;

                shell = Activator.CreateInstance(shellType);
                object bin = Invoke(shell, "Namespace", 10); // 10 = Recycle Bin
                if (bin == null) return false;

                object items = Invoke(bin, "Items");
                if (items == null) return false;

                int count = (int)Get(items, "Count");
                for (int i = 0; i < count; i++)
                {
                    object item = Invoke(items, "Item", i);
                    if (item == null) continue;

                    string name = Get(item, "Name") as string;
                    string from = Invoke(item, "ExtendedProperty", "System.Recycle.DeletedFrom") as string;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(from)) continue;

                    string full;
                    try { full = Path.Combine(from, name); }
                    catch { continue; }

                    if (!string.Equals(full.TrimEnd('\\'), originalPath.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase)) continue;

                    if (InvokeVerb(item)) return WaitForRestore(originalPath);
                    return false;
                }
            }
            catch { return false; }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }
            return false;
        }

        private static bool InvokeVerb(object item)
        {
            // 优先按动词名找「还原 / Restore / 恢复」，找不到再退回 InvokeVerb
            try
            {
                object verbs = Invoke(item, "Verbs");
                if (verbs != null)
                {
                    int n = (int)Get(verbs, "Count");
                    for (int i = 0; i < n; i++)
                    {
                        object verb = Invoke(verbs, "Item", i);
                        if (verb == null) continue;
                        string vn = Get(verb, "Name") as string;
                        if (string.IsNullOrEmpty(vn)) continue;
                        if (vn == "还原" || vn == "恢复" ||
                            vn.Equals("Restore", StringComparison.OrdinalIgnoreCase))
                        {
                            Invoke(verb, "DoIt");
                            return true;
                        }
                    }
                }
            }
            catch { }

            try
            {
                Invoke(item, "InvokeVerb", "还原");
                return true;
            }
            catch { }

            try
            {
                Invoke(item, "InvokeVerb", "Restore");
                return true;
            }
            catch { }

            return false;
        }

        private static bool WaitForRestore(string path)
        {
            for (int i = 0; i < 40; i++)
            {
                if (File.Exists(path) || Directory.Exists(path)) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            return target.GetType().InvokeMember(method,
                BindingFlags.InvokeMethod, null, target, args);
        }

        private static object Get(object target, string property)
        {
            return target.GetType().InvokeMember(property,
                BindingFlags.GetProperty, null, target, null);
        }
    }
}
