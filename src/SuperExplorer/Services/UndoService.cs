using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SuperExplorer.Models;

namespace SuperExplorer.Services
{
    public class UndoResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }

        public static UndoResult Success(string msg) { return new UndoResult { Ok = true, Message = msg }; }
        public static UndoResult Fail(string msg) { return new UndoResult { Ok = false, Message = msg }; }
    }

    public static class UndoService
    {
        public const int Limit = 50;

        private static readonly LinkedList<UndoAction> _stack = new LinkedList<UndoAction>();
        private static readonly object _lock = new object();

        public static int Count
        {
            get { lock (_lock) { return _stack.Count; } }
        }

        public static void Push(UndoAction action)
        {
            if (action == null) return;
            lock (_lock)
            {
                _stack.AddLast(action);
                while (_stack.Count > Limit) _stack.RemoveFirst();
            }
        }

        public static void Clear()
        {
            lock (_lock) { _stack.Clear(); }
        }

        public static async Task<UndoResult> UndoLastAsync()
        {
            UndoAction action;
            lock (_lock)
            {
                if (_stack.Count == 0) return UndoResult.Fail("没有可撤销的操作");
                action = _stack.Last.Value;
            }

            var result = await Task.Run(() => Perform(action));

            if (result.Ok)
            {
                lock (_lock)
                {
                    if (_stack.Count > 0 && ReferenceEquals(_stack.Last.Value, action))
                        _stack.RemoveLast();
                }
            }
            return result;
        }

        private static UndoResult Perform(UndoAction a)
        {
            try
            {
                switch (a.Kind)
                {
                    case UndoKind.Move:
                        {
                            if (!File.Exists(a.TargetPath) && !Directory.Exists(a.TargetPath))
                                return UndoResult.Fail("被移动的文件已不在目标位置：" + a.TargetPath);

                            var destDir = Path.GetDirectoryName(a.SourcePath);
                            if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
                                return UndoResult.Fail("原位置已不存在：" + destDir);

                            FileSystemService.MoveItem(a.TargetPath, destDir);
                            return UndoResult.Success("已撤销移动：" + a.DisplayName);
                        }

                    case UndoKind.Copy:
                        {
                            if (!File.Exists(a.TargetPath) && !Directory.Exists(a.TargetPath))
                                return UndoResult.Fail("副本已不存在：" + a.TargetPath);

                            if (!FileSystemService.DeleteToRecycleBin(a.TargetPath))
                                return UndoResult.Fail("删除副本失败：" + a.DisplayName);

                            return UndoResult.Success("已撤销复制：" + a.DisplayName);
                        }

                    case UndoKind.Delete:
                        {
                            if (File.Exists(a.SourcePath) || Directory.Exists(a.SourcePath))
                                return UndoResult.Fail("原位置已有同名文件，未执行还原：" + a.DisplayName);

                            if (!RecycleBinService.Restore(a.SourcePath))
                                return UndoResult.Fail("从回收站还原失败：" + a.DisplayName);

                            return UndoResult.Success("已从回收站还原：" + a.DisplayName);
                        }

                    case UndoKind.Rename:
                        {
                            if (!File.Exists(a.TargetPath) && !Directory.Exists(a.TargetPath))
                                return UndoResult.Fail("改名后的文件已不存在：" + a.TargetPath);

                            if (File.Exists(a.SourcePath) || Directory.Exists(a.SourcePath))
                                return UndoResult.Fail("原名称已被占用：" + a.SourcePath);

                            var err = FileSystemService.RenameItem(a.TargetPath, Path.GetFileName(a.SourcePath));
                            if (err != null) return UndoResult.Fail(err);

                            return UndoResult.Success("已撤销重命名：" + a.DisplayName);
                        }

                    case UndoKind.BatchRename:
                        {
                            var children = a.Children;
                            if (children == null || children.Count == 0)
                                return UndoResult.Fail("空操作");

                            // 按改名的相反顺序还原：链式换名时先还原的后项会腾出名字给前项
                            int ok = 0;
                            string firstErr = null;
                            for (int i = children.Count - 1; i >= 0; i--)
                            {
                                var c = children[i];
                                if (!File.Exists(c.TargetPath) && !Directory.Exists(c.TargetPath))
                                {
                                    firstErr = firstErr ?? ("改名后的文件已不存在：" + c.TargetPath);
                                    continue;
                                }
                                if (File.Exists(c.SourcePath) || Directory.Exists(c.SourcePath))
                                {
                                    firstErr = firstErr ?? ("原名称已被占用：" + c.SourcePath);
                                    continue;
                                }
                                var err = FileSystemService.RenameItem(c.TargetPath, Path.GetFileName(c.SourcePath));
                                if (err == null) ok++;
                                else firstErr = firstErr ?? err;
                            }

                            if (ok == children.Count)
                                return UndoResult.Success("已整体撤销批量重命名（" + children.Count + " 项）：" + a.DisplayName);

                            return UndoResult.Fail("批量撤销部分失败（" + ok + "/" + children.Count +
                                "），再次 Ctrl+Z 可重试剩余项" +
                                (firstErr == null ? "" : "：" + firstErr));
                        }

                    case UndoKind.NewFolder:
                        {
                            if (!Directory.Exists(a.SourcePath))
                                return UndoResult.Fail("文件夹已不存在：" + a.SourcePath);

                            if (!FileSystemService.DeleteToRecycleBin(a.SourcePath))
                                return UndoResult.Fail("删除新建文件夹失败：" + a.DisplayName);

                            return UndoResult.Success("已撤销新建文件夹：" + a.DisplayName);
                        }
                }
            }
            catch (Exception ex)
            {
                return UndoResult.Fail(ex.Message);
            }

            return UndoResult.Fail("未知的撤销类型");
        }
    }
}
