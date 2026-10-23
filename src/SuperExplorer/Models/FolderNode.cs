using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace SuperExplorer.Models
{
    public enum NodeKind
    {
        Pc,
        Drive,
        Folder
    }

    public class FolderNode : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public NodeKind Kind { get; set; }
        public FolderNode Parent { get; set; }

        public ObservableCollection<FolderNode> Children { get; } = new ObservableCollection<FolderNode>();

        public bool ChildrenLoaded { get; set; }

        private int _depth;
        public int Depth
        {
            get { return _depth; }
            set
            {
                _depth = value;
                OnPropertyChanged(nameof(Depth));
                OnPropertyChanged(nameof(Indent));
            }
        }

        public Thickness Indent
        {
            get { return new Thickness(4 + _depth * 14, 0, 0, 0); }
        }

        private bool _hasChildren = true;
        public bool HasChildren
        {
            get { return _hasChildren; }
            set
            {
                _hasChildren = value;
                OnPropertyChanged(nameof(HasChildren));
                OnPropertyChanged(nameof(Glyph));
            }
        }

        private bool _expanded;
        public bool IsExpanded
        {
            get { return _expanded; }
            set
            {
                _expanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(Glyph));
            }
        }

        public string Glyph
        {
            get { return !_hasChildren ? "" : (_expanded ? "▾" : "▸"); }
        }

        private long _size;
        public long Size
        {
            get { return _size; }
            set
            {
                if (_size == value) return;
                _size = value;
                OnPropertyChanged(nameof(Size));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(ShowBar));
            }
        }

        public long TotalSize { get; set; }

        private double _ratio;
        public double SizeRatio
        {
            get { return _ratio; }
            set
            {
                _ratio = value;
                OnPropertyChanged(nameof(SizeRatio));
            }
        }

        public bool ShowBar
        {
            get { return Kind == NodeKind.Drive && TotalSize > 0; }
        }

        public string SizeText
        {
            get
            {
                if (Kind == NodeKind.Drive && TotalSize > 0)
                {
                    return Gb(Size) + " / " + Gb(TotalSize);
                }
                if (_size <= 0) return "";
                return Gb(_size);
            }
        }

        private static string Gb(long bytes)
        {
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
        }

        private bool _favorite;
        public bool IsFavorite
        {
            get { return _favorite; }
            set
            {
                _favorite = value;
                OnPropertyChanged(nameof(IsFavorite));
                OnPropertyChanged(nameof(FavoriteGlyph));
            }
        }

        public string FavoriteGlyph
        {
            get { return _favorite ? "★" : "☆"; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
