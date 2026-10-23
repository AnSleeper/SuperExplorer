using System;

namespace SuperExplorer.Models
{
    public enum UndoKind
    {
        Move,
        Copy,
        Delete,
        Rename,
        NewFolder
    }

    public class UndoAction
    {
        public UndoKind Kind { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;

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
