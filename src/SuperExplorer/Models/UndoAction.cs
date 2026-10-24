using System;
using System.Collections.Generic;

namespace SuperExplorer.Models
{
    public enum UndoKind
    {
        Move,
        Copy,
        Delete,
        Rename,
        NewFolder,
        BatchRename,
        // 组合操作：一次批量动作（多选删除/移动/复制）整体入栈、整体撤销
        Composite
    }

    public class UndoAction
    {
        public UndoKind Kind { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>批量操作的子操作（如批量重命名的每一项），整体进撤销栈、整体撤销。</summary>
        public List<UndoAction> Children { get; set; }

        public string DisplayName
        {
            get
            {
                var p = Kind == UndoKind.Copy ? TargetPath : SourcePath;
                try { return System.IO.Path.GetFileName(p.TrimEnd('\\')); }
                catch { return p; }
            }
        }
    }
}
