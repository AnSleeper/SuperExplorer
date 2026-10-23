using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SuperExplorer.Models;

namespace SuperExplorer.Services
{
    public static class ThumbnailService
    {
        // 文件夹缩略图走系统提取，并发限制 3，避免大量目录同时枚举拖慢磁盘
        private static readonly SemaphoreSlim _shellGate = new SemaphoreSlim(3, 3);
        private const int FolderCacheLimit = 200;
        private const int NoThumbTtlMs = 60000;

        private static int _pending;
        private static readonly ConcurrentDictionary<string, byte> _inflight =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // 文件夹缩略图按「路径@尺寸」缓存，换目录回来不必重新提取
        private static readonly ConcurrentDictionary<string, BitmapSource> _folderCache =
            new ConcurrentDictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        // 没有可用预览的文件夹记下来，避免反复请求；带过期时间，临时失败可以自愈
        private static readonly ConcurrentDictionary<string, long> _folderNoThumb =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static bool IsBusy { get { return _pending > 0; } }

        public static void Request(FileEntry entry, int decodeWidth)
        {
            if (entry == null) return;
            if (entry.Kind == EntryKind.Text) return;
            if (entry.ThumbFailed) return;
            if (entry.Thumbnail != null) return;

            int width = Math.Max(64, decodeWidth);
            string key = entry.FullPath + "@" + width;

            if (entry.Kind == EntryKind.Folder)
            {
                BitmapSource cached;
                if (_folderCache.TryGetValue(key, out cached))
                {
                    entry.Thumbnail = cached;
                    return;
                }

                long stamp;
                if (_folderNoThumb.TryGetValue(key, out stamp) &&
                    Environment.TickCount64 - stamp < NoThumbTtlMs)
                {
                    entry.ThumbFailed = true;
                    return;
                }

                // 之前失败过但已过期：清掉标记，重新尝试
                entry.ThumbFailed = false;
            }

            if (!_inflight.TryAdd(key, 0)) return;

            entry.ThumbRequested = true;
            _pending++;

            Task.Run(() =>
            {
                BitmapSource bmp = null;
                try
                {
                    if (entry.Kind == EntryKind.Video || entry.Kind == EntryKind.Folder)
                    {
                        _shellGate.Wait();
                        try
                        {
                            // 文件夹只要真实预览，且不放大返回尺寸，避免拿到 256px 大图吃内存
                            bmp = LoadShellThumbnail(entry.FullPath, width, entry.Kind == EntryKind.Folder);
                        }
                        finally
                        {
                            _shellGate.Release();
                        }
                    }
                    else
                    {
                        bmp = LoadImage(entry.FullPath, width);
                    }
                }
                catch
                {
                    bmp = null;
                }

                if (entry.Kind == EntryKind.Folder)
                {
                    if (bmp == null)
                    {
                        if (_folderNoThumb.Count > 4000) _folderNoThumb.Clear();
                        _folderNoThumb[key] = Environment.TickCount64;
                        entry.ThumbFailed = true;
                    }
                    else
                    {
                        if (_folderCache.Count >= FolderCacheLimit) _folderCache.Clear();
                        _folderCache[key] = bmp;
                    }
                }
                else if (bmp == null)
                {
                    entry.ThumbFailed = true;
                }

                _pending--;
                entry.ThumbRequested = false;
                _inflight.TryRemove(key, out _);

                if (bmp == null) return;

                var app = Application.Current;
                if (app == null) return;

                _ = app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    entry.Thumbnail = bmp;
                }));
            });
        }

        private static BitmapSource LoadImage(string path, int decodeWidth)
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = Math.Max(64, decodeWidth);
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        // ---------- Windows Shell 缩略图（视频首帧、文件夹预览） ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private static BitmapSource LoadShellThumbnail(string path, int size, bool exactSize)
        {
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero,
                    typeof(IShellItemImageFactory).GUID, out factory);

                var sz = new SIZE { cx = size, cy = size };
                var flags = SIIGBF.THUMBNAILONLY | (exactSize ? 0 : SIIGBF.BIGGERSIZEOK);
                if (factory.GetImage(sz, flags, out hbm) != 0)
                    return null;
                if (hbm == IntPtr.Zero) return null;

                var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hbm, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory);
            }
        }
    }
}
