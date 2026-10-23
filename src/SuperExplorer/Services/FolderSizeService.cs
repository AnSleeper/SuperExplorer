using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SuperExplorer.Models;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 文件夹容量按需后台统计：只统计当前树里已经展开、可见的节点，
    /// 并发限制 2，避免大量磁盘扫描拖慢主工作流。
    /// </summary>
    public static class FolderSizeService
    {
        private static readonly ConcurrentDictionary<string, long> _cache =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, byte> _inflight =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(2);

        public static void Request(FolderNode node)
        {
            if (node == null || node.Kind != NodeKind.Folder) return;
            if (string.IsNullOrEmpty(node.FullPath)) return;

            if (_cache.TryGetValue(node.FullPath, out var cached))
            {
                node.Size = cached;
                return;
            }

            if (!_inflight.TryAdd(node.FullPath, 0)) return;

            var path = node.FullPath;
            _ = Task.Run(async () =>
            {
                long size = 0;
                await _gate.WaitAsync();
                try { size = Compute(path, 0); }
                catch { }
                finally { _gate.Release(); }

                _cache[path] = size;
                _inflight.TryRemove(path, out _);

                var app = Application.Current;
                if (app == null) return;

                _ = app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    node.Size = size;
                }));
            });
        }

        public static void ClearCache()
        {
            _cache.Clear();
        }

        private static long Compute(string path, int depth)
        {
            if (depth > 24) return 0;

            long total = 0;
            DirectoryInfo di;
            try { di = new DirectoryInfo(path); }
            catch { return 0; }

            try
            {
                foreach (var f in di.EnumerateFiles())
                {
                    try { total += f.Length; } catch { }
                }
            }
            catch { }

            try
            {
                foreach (var d in di.EnumerateDirectories())
                {
                    try
                    {
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }

                    total += Compute(d.FullName, depth + 1);
                }
            }
            catch { }

            return total;
        }
    }
}
