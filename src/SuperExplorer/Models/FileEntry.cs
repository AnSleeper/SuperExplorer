using System;
using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace SuperExplorer.Models
{
    public enum EntryKind
    {
        Folder,
        Image,
        Gif,
        Video,
        Text,
        Other
    }

    public class FileEntry : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public EntryKind Kind { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string Ext { get; set; }

        // 分辨率 / 时长 / 行数 等一行副标题
        public string Meta { get; set; }

        // 文本文件的前几行
        public string TextPreview { get; set; }

        public bool ThumbRequested { get; set; }
        public bool ThumbFailed { get; set; }

        public bool IsFolder
        {
            get { return Kind == EntryKind.Folder; }
        }

        private bool _selected;
        public bool IsSelected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        private bool _renaming;
        public bool IsRenaming
        {
            get { return _renaming; }
            set
            {
                if (_renaming == value) return;
                _renaming = value;
                OnPropertyChanged(nameof(IsRenaming));
            }
        }

        private BitmapSource _thumb;

        public BitmapSource Thumbnail
        {
            get { return _thumb; }
            set
            {
                _thumb = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }

        public string SizeText
        {
            get
            {
                if (Size < 1024) return Size + " B";
                if (Size < 1024 * 1024) return (Size / 1024.0).ToString("0.0") + " KB";
                if (Size < 1024L * 1024 * 1024) return (Size / 1024.0 / 1024.0).ToString("0.0") + " MB";
                return (Size / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
