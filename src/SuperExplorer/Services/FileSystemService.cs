using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SuperExplorer.Models;

namespace SuperExplorer.Services
{
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);

        public int Compare(string x, string y)
        {
            return StrCmpLogicalW(x, y);
        }
    }

    public static class FileSystemService
    {
        private static readonly HashSet<string> ImageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".avif", ".ico"
        };

        private static readonly HashSet<string> GifExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".gif"
        };

        private static readonly HashSet<string> VideoExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".m4v"
        };

        private static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".log", ".json", ".xml", ".csv"
        };

        public static EntryKind KindOf(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return EntryKind.Other;
            if (ImageExt.Contains(ext)) return EntryKind.Image;
            if (GifExt.Contains(ext)) return EntryKind.Gif;
            if (VideoExt.Contains(ext)) return EntryKind.Video;
            if (TextExt.Contains(ext)) return EntryKind.Text;
            return EntryKind.Other;
        }

        public static List<FolderNode> GetDrives()
        {
            var list = new List<FolderNode>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return list; }

            foreach (var d in drives)
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;

                    var letter = d.Name.Substring(0, 2);
                    var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel;
                    var used = d.TotalSize - d.TotalFreeSpace;
                    list.Add(new FolderNode
                    {
                        Name = label + " (" + letter + ")",
                        FullPath = d.RootDirectory.FullName,
                        Kind = NodeKind.Drive,
                        Size = used,
                        TotalSize = d.TotalSize,
                        SizeRatio = d.TotalSize > 0 ? (double)used / d.TotalSize : 0
                    });
                }
                catch { }
            }
            return list;
        }

        public static List<FolderNode> GetChildFolders(FolderNode parent)
        {
            var list = new List<FolderNode>();
            if (parent == null || string.IsNullOrEmpty(parent.FullPath)) return list;

            DirectoryInfo di;
            try { di = new DirectoryInfo(parent.FullPath); }
            catch { return list; }

            DirectoryInfo[] dirs;
            try { dirs = di.GetDirectories(); }
            catch { return list; }

            foreach (var d in dirs.OrderBy(x => x.Name, NaturalComparer.Instance))
            {
                // 跳过系统隐藏目录，避免噪音
                try
                {
                    if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                }
                catch { }

                list.Add(new FolderNode
                {
                    Name = d.Name,
                    FullPath = d.FullName,
                    Kind = NodeKind.Folder,
                    Parent = parent
                });
            }
            return list;
        }

        public static List<FileEntry> GetEntries(string path)
        {
            var list = new List<FileEntry>();
            DirectoryInfo di;
            try { di = new DirectoryInfo(path); }
            catch { return list; }

            FileSystemInfo[] infos;
            try { infos = di.GetFileSystemInfos(); }
            catch { return list; }

            foreach (var d in infos.OfType<DirectoryInfo>().OrderBy(x => x.Name, NaturalComparer.Instance))
            {
                try
                {
                    if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                    list.Add(new FileEntry
                    {
                        Name = d.Name,
                        FullPath = d.FullName,
                        Kind = EntryKind.Folder,
                        Modified = d.LastWriteTime,
                        Ext = "",
                        Meta = "文件夹"
                    });
                }
                catch { }
            }

            foreach (var f in infos.OfType<FileInfo>().OrderBy(x => x.Name, NaturalComparer.Instance))
            {
                try
                {
                    if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                    var kind = KindOf(f.Extension);
                    if (kind == EntryKind.Other) continue;

                    var entry = new FileEntry
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        Kind = kind,
                        Size = f.Length,
                        Modified = f.LastWriteTime,
                        Ext = f.Extension.TrimStart('.').ToUpperInvariant(),
                        Meta = FormatSize(f.Length)
                    };

                    if (kind == EntryKind.Text)
                        entry.TextPreview = ReadTextPreview(f.FullName);

                    list.Add(entry);
                }
                catch { }
            }

            return list;
        }

        // ---------- 递归搜索（当前面板范围，按需扫描不建索引） ----------

        public static List<FileEntry> SearchEntries(string root, string term,
            int maxResults, int maxDepth, System.Threading.CancellationToken ct)
        {
            var results = new List<FileEntry>();
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(term)) return results;

            try
            {
                WalkSearch(new DirectoryInfo(root), "", 0, term.Trim(), maxResults, maxDepth, ct, results);
            }
            catch (OperationCanceledException) { }
            catch { }

            return results;
        }

        private static void WalkSearch(DirectoryInfo dir, string rel, int depth, string term,
            int maxResults, int maxDepth, System.Threading.CancellationToken ct, List<FileEntry> results)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= maxResults || depth > maxDepth) return;

            FileSystemInfo[] infos;
            try { infos = dir.GetFileSystemInfos(); }
            catch { return; }

            foreach (var info in infos)
            {
                if (results.Count >= maxResults) return;
                try
                {
                    if ((info.Attributes & FileAttributes.Hidden) != 0) continue;

                    var d = info as DirectoryInfo;
                    if (d != null)
                    {
                        if (d.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            results.Add(new FileEntry
                            {
                                Name = d.Name,
                                FullPath = d.FullName,
                                Kind = EntryKind.Folder,
                                Modified = d.LastWriteTime,
                                Ext = "",
                                Meta = rel.Length == 0 ? "文件夹" : rel
                            });
                        }

                        // 跳过链接目录，避免死循环与重复统计
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0)
                            WalkSearch(d, rel.Length == 0 ? d.Name : rel + "\\" + d.Name,
                                depth + 1, term, maxResults, maxDepth, ct, results);
                    }
                    else
                    {
                        var f = (FileInfo)info;
                        var kind = KindOf(f.Extension);
                        if (kind == EntryKind.Other) continue;

                        if (f.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            results.Add(new FileEntry
                            {
                                Name = f.Name,
                                FullPath = f.FullName,
                                Kind = kind,
                                Size = f.Length,
                                Modified = f.LastWriteTime,
                                Ext = f.Extension.TrimStart('.').ToUpperInvariant(),
                                Meta = rel.Length == 0 ? FormatSize(f.Length) : rel
                            });
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 面板内容排序：文件夹永远排在文件前面（内部按名称自然排序），
        /// 文件按指定字段排，同名退化按自然名称排。纯内存操作，零额外开销。
        /// </summary>
        public static void ApplySort(List<FileEntry> list, string sortBy, bool asc)
        {
            list.Sort((a, b) =>
            {
                if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
                if (a.IsFolder) return NaturalComparer.Instance.Compare(a.Name, b.Name);

                int r;
                switch (sortBy)
                {
                    case "date": r = a.Modified.CompareTo(b.Modified); break;
                    case "type": r = string.CompareOrdinal(a.Ext ?? "", b.Ext ?? ""); break;
                    case "size": r = a.Size.CompareTo(b.Size); break;
                    default: r = 0; break;
                }
                if (r == 0) r = NaturalComparer.Instance.Compare(a.Name, b.Name);
                return asc ? r : -r;
            });
        }

        private static string FormatSize(long size)
        {
            if (size < 1024) return size + " B";
            if (size < 1024 * 1024) return (size / 1024.0).ToString("0.0") + " KB";
            if (size < 1024L * 1024 * 1024) return (size / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (size / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        // 只取前 10 行做预览，不再读完整文件统计行数
        private static string ReadTextPreview(string path)
        {
            try
            {
                using (var sr = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
                {
                    var head = new List<string>(10);
                    for (int i = 0; i < 10; i++)
                    {
                        var line = sr.ReadLine();
                        if (line == null) break;
                        head.Add(line);
                    }
                    return string.Join("\n", head);
                }
            }
            catch { return ""; }
        }

        public static long GetFolderSize(string path)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(path))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(path))
                {
                    try { total += GetFolderSize(d); } catch { }
                }
            }
            catch { }
            return total;
        }

        // ---------- 删除到回收站 ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        public static bool DeleteToRecycleBin(string path)
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x0040 | 0x0010 | 0x0004 | 0x0400 // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
                };
                return SHFileOperation(ref op) == 0;
            }
            catch { return false; }
        }

        // ---------- 移动 / 复制 ----------

        public static string RenameItem(string path, string newName)
        {
            try
            {
                newName = (newName ?? "").Trim();
                if (newName.Length == 0) return "名称不能为空";
                if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称包含非法字符";

                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return "无法重命名";

                var target = Path.Combine(dir, newName);
                if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return null;
                if (File.Exists(target) || Directory.Exists(target)) return "已存在同名文件或文件夹";

                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
                return null;
            }
            catch (Exception ex)
            {
                return "重命名失败：" + ex.Message;
            }
        }

        public static string CreateFolder(string parent, string name, out string error)
        {
            error = null;
            try
            {
                var target = Path.Combine(parent, name);
                for (int i = 2; File.Exists(target) || Directory.Exists(target); i++)
                    target = Path.Combine(parent, name + " (" + i + ")");

                Directory.CreateDirectory(target);
                return target;
            }
            catch (Exception ex)
            {
                error = "新建文件夹失败：" + ex.Message;
                return null;
            }
        }

        public static string UniquePath(string target)
        {
            if (!File.Exists(target) && !Directory.Exists(target)) return target;

            var dir = Path.GetDirectoryName(target);
            var name = Path.GetFileNameWithoutExtension(target);
            var ext = Path.GetExtension(target);

            for (int i = 2; i < 10000; i++)
            {
                var candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            throw new IOException("无法生成唯一文件名");
        }

        public static string MoveItem(string source, string destDir)
        {
            var dest = UniquePath(Path.Combine(destDir, Path.GetFileName(source)));
            var sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(dest),
                StringComparison.OrdinalIgnoreCase);

            if (Directory.Exists(source))
            {
                if (sameVolume) Directory.Move(source, dest);
                else { CopyDirectory(source, dest); Directory.Delete(source, true); }
            }
            else
            {
                if (sameVolume) File.Move(source, dest);
                else { File.Copy(source, dest, false); File.Delete(source); }
            }
            return dest;
        }

        public static string CopyItem(string source, string destDir)
        {
            var dest = UniquePath(Path.Combine(destDir, Path.GetFileName(source)));
            if (Directory.Exists(source)) CopyDirectory(source, dest);
            else File.Copy(source, dest, false);
            return dest;
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.EnumerateFiles(source))
            {
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), false);
            }
            foreach (var d in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
            }
        }

        public static bool IsSubPathOf(string child, string parent)
        {
            try
            {
                var c = Path.GetFullPath(child).TrimEnd('\\') + "\\";
                var p = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static bool SameVolume(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        public static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void OpenInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static void LocateInExplorer(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + filePath + "\"",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public static FileEntry EntryFromPath(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    return new FileEntry
                    {
                        Name = di.Name,
                        FullPath = di.FullName,
                        Kind = EntryKind.Folder,
                        Modified = di.LastWriteTime,
                        Ext = "",
                        Meta = "文件夹"
                    };
                }

                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                var kind = KindOf(fi.Extension);
                var entry = new FileEntry
                {
                    Name = fi.Name,
                    FullPath = fi.FullName,
                    Kind = kind,
                    Size = fi.Length,
                    Modified = fi.LastWriteTime,
                    Ext = fi.Extension.TrimStart('.').ToUpperInvariant(),
                    Meta = FormatSize(fi.Length)
                };
                return entry;
            }
            catch { return null; }
        }
    }
}
