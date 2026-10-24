using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SuperExplorer.Models;
using SuperExplorer.Services;

namespace SuperExplorer.Controls
{
    public partial class TileView : UserControl
    {
        public static readonly RoutedEvent DeleteRequestedEvent = EventManager.RegisterRoutedEvent(
            "DeleteRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileView));

        public static readonly RoutedEvent OpenRequestedEvent = EventManager.RegisterRoutedEvent(
            "OpenRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileView));

        public static readonly RoutedEvent LocateRequestedEvent = EventManager.RegisterRoutedEvent(
            "LocateRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileView));

        public static readonly RoutedEvent SelectedEvent = EventManager.RegisterRoutedEvent(
            "Selected", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileView));

        public static readonly RoutedEvent FolderDropRequestedEvent = EventManager.RegisterRoutedEvent(
            "FolderDropRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(TileView));

        /// <summary>携带拖拽原始数据的路由参数，用于「拖到文件夹缩略图上移入该文件夹」。</summary>
        public class TileDropEventArgs : RoutedEventArgs
        {
            public IDataObject Data { get; private set; }
            public DragDropKeyStates KeyStates { get; private set; }

            public TileDropEventArgs(RoutedEvent routedEvent, object source,
                IDataObject data, DragDropKeyStates keyStates)
                : base(routedEvent, source)
            {
                Data = data;
                KeyStates = keyStates;
            }
        }

        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
            "IsSelected", typeof(bool), typeof(TileView),
            new PropertyMetadata(false, OnSelectedChanged));

        public event RoutedEventHandler DeleteRequested
        {
            add { AddHandler(DeleteRequestedEvent, value); }
            remove { RemoveHandler(DeleteRequestedEvent, value); }
        }

        public event RoutedEventHandler OpenRequested
        {
            add { AddHandler(OpenRequestedEvent, value); }
            remove { RemoveHandler(OpenRequestedEvent, value); }
        }

        public event RoutedEventHandler LocateRequested
        {
            add { AddHandler(LocateRequestedEvent, value); }
            remove { RemoveHandler(LocateRequestedEvent, value); }
        }

        public event RoutedEventHandler Selected
        {
            add { AddHandler(SelectedEvent, value); }
            remove { RemoveHandler(SelectedEvent, value); }
        }

        public event Action<FileEntry, string> RenameCommitted;
        public event Action NewFolderRequested;

        public bool IsSelected
        {
            get { return (bool)GetValue(IsSelectedProperty); }
            set { SetValue(IsSelectedProperty, value); }
        }

        private Point _dragStart;
        private bool _dragging;
        private bool _thumbDeferred;

        public TileView()
        {
            InitializeComponent();

            DataContextChanged += (s, e) =>
            {
                UpdateVisual();
                RequestThumbIfNeeded();
            };
            Loaded += (s, e) => RequestThumbIfNeeded();
            Unloaded += (s, e) => UnhookOutside();

            PreviewMouseLeftButtonDown += OnDown;
            PreviewMouseRightButtonDown += (s, e) =>
            {
                RaiseEvent(new RoutedEventArgs(SelectedEvent, this));
            };
            MouseDoubleClick += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                var entry = DataContext as FileEntry;
                if (entry != null && entry.IsRenaming) return;
                RaiseEvent(new RoutedEventArgs(OpenRequestedEvent, this));
            };
            PreviewMouseMove += OnMove;
        }

        private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var tile = d as TileView;
            if (tile == null) return;
            tile.ApplySelection();
        }

        private void ApplySelection()
        {
            if (Bd == null) return;
            if (IsSelected)
            {
                Bd.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                Bd.BorderThickness = new Thickness(2);
            }
            else
            {
                Bd.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                Bd.BorderThickness = new Thickness(1);
            }
        }

        // ---------- 作为拖放目标：文件/文件夹拖到文件夹缩略图上 = 移入该文件夹 ----------

        private bool _dropHighlighted;

        private bool CanAcceptTileDrop(IDataObject data)
        {
            var entry = DataContext as FileEntry;
            if (entry == null || entry.Kind != EntryKind.Folder) return false;
            if (data == null) return false;
            if (!data.GetDataPresent("SuperExplorer.FileEntry") &&
                !data.GetDataPresent(DataFormats.FileDrop)) return false;
            return true;
        }

        private void SetDropHighlight(bool on)
        {
            if (Bd == null || _dropHighlighted == on) return;
            _dropHighlighted = on;
            if (on)
            {
                Bd.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                Bd.BorderThickness = new Thickness(2);
            }
            else
            {
                ApplySelection();
            }
        }

        private void Bd_DragOver(object sender, DragEventArgs e)
        {
            if (CanAcceptTileDrop(e.Data))
            {
                SetDropHighlight(true);
                e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
            }
            else
            {
                SetDropHighlight(false);
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Bd_DragLeave(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
        }

        private void Bd_Drop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            if (!CanAcceptTileDrop(e.Data)) return;

            RaiseEvent(new TileDropEventArgs(FolderDropRequestedEvent, this, e.Data, e.KeyStates));
            e.Handled = true;
        }

        private void RequestThumbIfNeeded()
        {
            if (!IsLoaded) return;

            var entry = DataContext as FileEntry;
            if (entry == null) return;
            if (entry.Kind == EntryKind.Text) return;
            if (entry.Thumbnail != null) return;

            // 卡片刚创建时宽度还是 0，等布局完成拿到真实宽度再请求，避免按 64px 提取导致发虚
            double w = ActualWidth;
            if (w < 40)
            {
                if (_thumbDeferred) return;
                _thumbDeferred = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _thumbDeferred = false;
                    RequestThumbIfNeeded();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            ThumbnailService.Request(entry, (int)w);
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragging = false;
            RaiseEvent(new RoutedEventArgs(SelectedEvent, this));
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragging) return;
            if (RenameBox.IsKeyboardFocusWithin) return;

            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;

            var entry = DataContext as FileEntry;
            if (entry == null) return;

            _dragging = true;
            try
            {
                var data = new DataObject("SuperExplorer.FileEntry", entry);
                DragDrop.DoDragDrop(this, data, DragDropEffects.Move | DragDropEffects.Copy);
            }
            catch { }
            finally { _dragging = false; }
        }

        private FileEntry _watched;
        private Window _hookWindow;

        private void UpdateVisual()
        {
            var e = DataContext as FileEntry;

            if (_watched != null) _watched.PropertyChanged -= OnEntryChanged;
            _watched = e;
            if (_watched != null) _watched.PropertyChanged += OnEntryChanged;

            ApplyKindVisual(e);
            ApplyRenaming();
        }

        private void OnEntryChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Thumbnail")
                ApplyKindVisual(DataContext as FileEntry);
            else if (e.PropertyName == "IsRenaming")
                ApplyRenaming();
        }

        private void ApplyRenaming()
        {
            var entry = DataContext as FileEntry;
            bool on = entry != null && entry.IsRenaming;
            bool visible = RenameBox.Visibility == Visibility.Visible;
            if (on == visible) return;

            NameText.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            RenameBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (!on)
            {
                UnhookOutside();
                return;
            }

            HookOutside();
            RenameBox.Text = entry.Name;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RenameBox.Focus();
                int dot = entry.Kind == EntryKind.Folder ? -1 : entry.Name.LastIndexOf('.');
                if (dot > 0) RenameBox.Select(0, dot);
                else RenameBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // 点击卡片外的任何位置（其他文件、左树、空白处）都提交并退出重命名
        private void HookOutside()
        {
            if (_hookWindow != null) return;
            _hookWindow = Window.GetWindow(this);
            if (_hookWindow != null) _hookWindow.PreviewMouseDown += OnWindowMouseDown;
        }

        private void UnhookOutside()
        {
            if (_hookWindow == null) return;
            _hookWindow.PreviewMouseDown -= OnWindowMouseDown;
            _hookWindow = null;
        }

        private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (RenameBox.Visibility != Visibility.Visible) return;

            var p = e.GetPosition(RenameBox);
            if (p.X >= 0 && p.Y >= 0 && p.X <= RenameBox.ActualWidth && p.Y <= RenameBox.ActualHeight)
                return;

            CommitRename();
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitRename();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelRename();
            }
        }

        private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CommitRename();
        }

        private void CommitRename()
        {
            var entry = DataContext as FileEntry;
            if (entry == null || !entry.IsRenaming) return;

            var name = (RenameBox.Text ?? "").Trim();
            entry.IsRenaming = false;

            if (name.Length == 0 || name == entry.Name) return;
            RenameCommitted?.Invoke(entry, name);
        }

        private void CancelRename()
        {
            var entry = DataContext as FileEntry;
            if (entry == null) return;
            entry.IsRenaming = false;
        }

        private void ApplyKindVisual(FileEntry e)
        {
            if (e == null) return;

            bool folder = e.Kind == EntryKind.Folder;
            bool text = e.Kind == EntryKind.Text;
            bool folderThumb = folder && e.Thumbnail != null;

            FolderIcon.Visibility = folder && !folderThumb ? Visibility.Visible : Visibility.Collapsed;
            TextPrev.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            PlayBadge.Visibility = e.Kind == EntryKind.Video ? Visibility.Visible : Visibility.Collapsed;
            Thumb.Visibility = folder ? (folderThumb ? Visibility.Visible : Visibility.Collapsed)
                                      : (text ? Visibility.Collapsed : Visibility.Visible);
            Placeholder.Visibility = folder ? (folderThumb ? Visibility.Collapsed : Visibility.Visible)
                                            : (text ? Visibility.Collapsed : Visibility.Visible);

            Placeholder.Text = folder ? "" : (string.IsNullOrEmpty(e.Ext) ? "FILE" : e.Ext);
            ApplySelection();
        }

        private void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(OpenRequestedEvent, this));
        }

        private void MenuLocate_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(LocateRequestedEvent, this));
        }

        private void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(DeleteRequestedEvent, this));
        }

        private void MenuRename_Click(object sender, RoutedEventArgs e)
        {
            var entry = DataContext as FileEntry;
            if (entry != null) entry.IsRenaming = true;
        }

        private void MenuNewFolder_Click(object sender, RoutedEventArgs e)
        {
            NewFolderRequested?.Invoke();
        }
    }
}
