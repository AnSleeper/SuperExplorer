using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SuperExplorer.Models;
using SuperExplorer.Services;
using UndoAction = SuperExplorer.Models.UndoAction;

namespace SuperExplorer.Controls
{
    public partial class FolderPanel : UserControl
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            "IsActive", typeof(bool), typeof(FolderPanel),
            new PropertyMetadata(false, OnActiveChanged));

        public string FolderPath { get; private set; }

        public FileEntry SelectedEntry
        {
            get { return Grid.SelectedEntry; }
        }

        public event Action<FolderPanel> CloseRequested;
        public event Action<FolderPanel> Activated;
        public event Action<FolderPanel> Navigated;
        public event Action<FolderPanel, string> FolderActivated;
        public event Action<FolderPanel, FileEntry> FileActivated;
        public event Action<FolderPanel, FileEntry> DeleteRequested;
        public event Action<FolderPanel, FileEntry> LocateRequested;
        public event Action<FolderPanel, List<FileEntry>, bool> FilesDropped;
        public event Action<FolderPanel, string, List<FileEntry>, bool> FilesDroppedInto;
        public event Action<FolderPanel, List<string>> ExternalFolderDropped;
        public event Action<FolderPanel> FavoriteToggled;
        public event Action<FolderPanel, FolderPanel, bool> PanelDropped;
        public event Action<string> Notified;

        private int _loadToken;
        private string _lastLoadedPath;
        private Point _headerDown;
        private bool _headerCanDrag;

        public FolderPanel()
        {
            InitializeComponent();

            CloseBtn.Click += (s, e) => CloseRequested?.Invoke(this);
            ExplorerBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(FolderPath))
                    FileSystemService.OpenInExplorer(FolderPath);
            };

            FavBtn.Click += (s, e) => ToggleFavorite();

            Grid.EntryActivated += entry =>
            {
                if (entry.Kind == EntryKind.Folder) FolderActivated?.Invoke(this, entry.FullPath);
                else FileActivated?.Invoke(this, entry);
            };
            Grid.DeleteRequested += entry => DeleteRequested?.Invoke(this, entry);
            Grid.LocateRequested += entry => LocateRequested?.Invoke(this, entry);

            Grid.RenameRequested += (entry, name) =>
            {
                var dir = Path.GetDirectoryName(entry.FullPath);
                var oldPath = entry.FullPath;
                var err = FileSystemService.RenameItem(oldPath, name);
                if (err != null)
                {
                    Notified?.Invoke(err);
                    return;
                }

                if (string.IsNullOrEmpty(dir)) { _ = ReloadAsync(); return; }

                var newPath = Path.Combine(dir, name);
                UndoService.Push(new UndoAction
                {
                    Kind = UndoKind.Rename,
                    SourcePath = oldPath,
                    TargetPath = newPath
                });

                _ = ReloadAndSelectAsync(newPath, false);
            };

            Grid.NewFolderRequested += CreateNewFolder;

            // 文件/文件夹拖到文件夹缩略图上：移入该文件夹（而不是当前面板的文件夹）
            AddHandler(TileView.FolderDropRequestedEvent, new RoutedEventHandler(OnTileFolderDrop));
        }

        private void OnTileFolderDrop(object sender, RoutedEventArgs e)
        {
            var args = e as TileView.TileDropEventArgs;
            var tile = args == null ? null : args.OriginalSource as TileView;
            var target = tile == null ? null : tile.DataContext as FileEntry;
            if (args == null || target == null || target.Kind != EntryKind.Folder) return;

            e.Handled = true;

            // 复用面板的拖放数据提取：内部 FileEntry 或系统 FileDrop
            var entries = ExtractEntries(args.Data);
            if (entries.Count == 0) return;

            var filtered = new List<FileEntry>();
            foreach (var en in entries)
            {
                // 已经在目标文件夹里的不动；文件夹不能移进自己的子目录
                if (FileSystemService.SameFolder(Path.GetDirectoryName(en.FullPath), target.FullPath)) continue;
                if (en.Kind == EntryKind.Folder &&
                    FileSystemService.IsSubPathOf(target.FullPath, en.FullPath)) continue;
                filtered.Add(en);
            }
            if (filtered.Count == 0) return;

            bool copy = (args.KeyStates & DragDropKeyStates.ControlKey) != 0;
            FilesDroppedInto?.Invoke(this, target.FullPath, filtered, copy);
        }

        private static List<FileEntry> ExtractEntries(IDataObject data)
        {
            var list = new List<FileEntry>();
            if (data == null) return list;

            if (data.GetDataPresent("SuperExplorer.FileEntry"))
            {
                var entry = data.GetData("SuperExplorer.FileEntry") as FileEntry;
                if (entry != null) list.Add(entry);
            }
            else if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        var entry = FileSystemService.EntryFromPath(p);
                        if (entry != null) list.Add(entry);
                    }
                }
            }
            return list;
        }

        public void BeginRename()
        {
            Grid.BeginRename();
        }

        private void CreateNewFolder()
        {
            if (string.IsNullOrEmpty(FolderPath) || !Directory.Exists(FolderPath)) return;

            string error;
            var path = FileSystemService.CreateFolder(FolderPath, "新建文件夹", out error);
            if (path == null)
            {
                Notified?.Invoke(error ?? "新建文件夹失败");
                return;
            }

            UndoService.Push(new UndoAction { Kind = UndoKind.NewFolder, SourcePath = path });

            _ = ReloadAndSelectAsync(path, true);
        }

        private async System.Threading.Tasks.Task ReloadAndSelectAsync(string selectPath, bool startRename)
        {
            await ReloadAsync();
            Grid.SelectByPath(selectPath);
            if (startRename) Grid.BeginRename();
        }

        public bool IsActive
        {
            get { return (bool)GetValue(IsActiveProperty); }
            set { SetValue(IsActiveProperty, value); }
        }

        public double TileSize
        {
            set { Grid.TileSize = value; }
        }

        private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = d as FolderPanel;
            if (panel == null || panel.Shell == null) return;
            panel.Shell.BorderBrush = (Brush)Application.Current.Resources[
                (bool)e.NewValue ? "AccentBrush" : "BorderBrush"];
        }

        public void Load(string path, double tileSize)
        {
            FolderPath = path;
            Grid.TileSize = tileSize;
            BuildCrumbs();
            UpdateFavGlyph();
            _ = ReloadAsync();
        }

        public void Refresh()
        {
            _ = ReloadAsync();
        }

        public void RefreshLayout()
        {
            Grid.RefreshMetrics();
        }

        public void RefreshChrome()
        {
            BuildCrumbs();
            UpdateFavGlyph();
            Grid.RefreshMetrics();
        }

        private void ToggleFavorite()
        {
            if (string.IsNullOrEmpty(FolderPath)) return;

            var list = SettingsService.Current.Favorites;
            var existing = list.FirstOrDefault(p => string.Equals(p, FolderPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null) list.Remove(existing);
            else list.Add(FolderPath);

            SettingsService.Save();
            UpdateFavGlyph();
            FavoriteToggled?.Invoke(this);
        }

        public void RefreshFavoriteState()
        {
            UpdateFavGlyph();
        }

        private void UpdateFavGlyph()
        {
            bool fav = !string.IsNullOrEmpty(FolderPath) &&
                       SettingsService.Current.Favorites.Any(p =>
                           string.Equals(p, FolderPath, StringComparison.OrdinalIgnoreCase));

            FavBtn.Content = fav ? "★" : "☆";
            FavBtn.Foreground = (Brush)Application.Current.Resources[fav ? "AccentBrush" : "MutedBrush"];
            FavBtn.ToolTip = fav ? "取消常用" : "收藏为常用文件夹";
        }

        public void Navigate(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            FolderPath = path;
            BuildCrumbs();
            UpdateFavGlyph();
            _ = ReloadAsync();
            Navigated?.Invoke(this);
        }

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            int token = ++_loadToken;
            var path = FolderPath;

            // 切回正常浏览会清掉进行中的搜索

            _searchCts?.Cancel();

            MetaText.Text = "加载中…";

            List<FileEntry> entries;
            try
            {
                entries = await System.Threading.Tasks.Task.Run(() => FileSystemService.GetEntries(path));
            }
            catch
            {
                entries = new List<FileEntry>();
            }

            if (token != _loadToken) return;

            // 同一目录的刷新（重命名、删除后）保留滚动位置，切换目录则回到顶部
            bool same = string.Equals(path, _lastLoadedPath, StringComparison.OrdinalIgnoreCase);
            _lastLoadedPath = path;

            FileSystemService.ApplySort(entries,
                SettingsService.Current.SortBy ?? "name", SettingsService.Current.SortAsc);

            Grid.SetItems(entries, same);
            UpdateMeta(entries);
        }

        // ---------- 搜索（当前面板文件夹内递归） ----------

        private System.Threading.CancellationTokenSource _searchCts;

        public async void RunSearch(string term)
        {
            var t = (term ?? "").Trim();
            if (t.Length == 0)
            {
                ClearSearch();
                return;
            }

            _searchCts?.Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _searchCts = cts;


            var root = FolderPath;
            MetaText.Text = "正在搜索「" + t + "」…";

            List<FileEntry> list;
            try
            {
                list = await System.Threading.Tasks.Task.Run(() =>
                    FileSystemService.SearchEntries(root, t, 2000, 10, cts.Token));
            }
            catch
            {
                list = new List<FileEntry>();
            }

            if (cts.IsCancellationRequested) return;

            FileSystemService.ApplySort(list,
                SettingsService.Current.SortBy ?? "name", SettingsService.Current.SortAsc);

            Grid.SetItems(list, false);
            MetaText.Text = "搜索「" + t + "」：" + list.Count + " 项" +
                            (list.Count >= 2000 ? "（已达上限）" : "，Esc 清除");
        }

        public void ClearSearch()
        {

            _searchCts?.Cancel();
            _searchCts = null;
            _ = ReloadAsync();
        }

        private void UpdateMeta(List<FileEntry> entries)
        {
            long total = 0;
            foreach (var e in entries)
            {
                if (e.Kind != EntryKind.Folder) total += e.Size;
            }

            MetaText.Text = entries.Count + " 项 · " + SizeText(total);
        }

        private static string SizeText(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
        }

        private void BuildCrumbs()
        {
            CrumbHost.Children.Clear();
            if (string.IsNullOrEmpty(FolderPath)) return;

            var segments = new List<KeyValuePair<string, string>>();
            try
            {
                var full = Path.GetFullPath(FolderPath);
                var root = Path.GetPathRoot(full);
                segments.Add(new KeyValuePair<string, string>(root.TrimEnd('\\'), root));

                var rest = full.Substring(root.Length).TrimEnd('\\');
                if (rest.Length > 0)
                {
                    var acc = root.TrimEnd('\\');
                    foreach (var part in rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        acc = acc + "\\" + part;
                        segments.Add(new KeyValuePair<string, string>(part, acc));
                    }
                }
            }
            catch
            {
                segments.Add(new KeyValuePair<string, string>(FolderPath, FolderPath));
            }

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                bool isLast = i == segments.Count - 1;

                if (isLast)
                {
                    var tb = new TextBlock
                    {
                        Text = seg.Key,
                        FontSize = (double)Application.Current.Resources["FontLg"],
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)Application.Current.Resources["TextBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(3, 1, 3, 1),
                        ToolTip = seg.Value
                    };
                    CrumbHost.Children.Add(tb);
                }
                else
                {
                    var btn = new Button
                    {
                        Content = seg.Key,
                        Style = (Style)Application.Current.Resources["CrumbButton"],
                        ToolTip = seg.Value
                    };
                    var target = seg.Value;
                    btn.Click += (s, e) => Navigate(target);
                    CrumbHost.Children.Add(btn);

                    var sep = new TextBlock
                    {
                        Text = "›",
                        FontSize = (double)Application.Current.Resources["FontSm"],
                        Foreground = (Brush)Application.Current.Resources["DimBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(1, 0, 1, 0)
                    };
                    CrumbHost.Children.Add(sep);
                }
            }
        }

        // ---------- 面板标题拖拽换位 ----------

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            {
                _headerCanDrag = false;
                return;
            }
            _headerDown = e.GetPosition(this);
            _headerCanDrag = true;
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_headerCanDrag || e.LeftButton != MouseButtonState.Pressed) return;

            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _headerDown.X) < 8 && Math.Abs(p.Y - _headerDown.Y) < 8) return;

            _headerCanDrag = false;
            try
            {
                var data = new DataObject("SuperExplorer.Panel", this);
                DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
            }
            catch { }
        }

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T) return (T)d;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        // ---------- 拖放 ----------

        private void Shell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            Activated?.Invoke(this);
        }

        private void Shell_DragOver(object sender, DragEventArgs e)
        {
            Activated?.Invoke(this);

            if (e.Data.GetDataPresent("SuperExplorer.Panel"))
            {
                DropOverlay.Visibility = Visibility.Collapsed;
                var pos = e.GetPosition(Shell);
                bool after = pos.X > Shell.ActualWidth / 2;
                InsertMark.HorizontalAlignment = after ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                InsertMark.Margin = after
                    ? new Thickness(0, 6, 1, 6)
                    : new Thickness(1, 6, 0, 6);
                InsertMark.Visibility = Visibility.Visible;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (CanAcceptFiles(e.Data))
            {
                InsertMark.Visibility = Visibility.Collapsed;
                DropOverlay.Visibility = Visibility.Visible;
                e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;
        }

        private void Shell_DragLeave(object sender, DragEventArgs e)
        {
            if (Shell.IsMouseOver) return;
            DropOverlay.Visibility = Visibility.Collapsed;
            InsertMark.Visibility = Visibility.Collapsed;
        }

        private void Shell_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            InsertMark.Visibility = Visibility.Collapsed;

            if (e.Data.GetDataPresent("SuperExplorer.Panel"))
            {
                var dragged = e.Data.GetData("SuperExplorer.Panel") as FolderPanel;
                if (dragged != null && dragged != this)
                {
                    var pos = e.GetPosition(Shell);
                    bool after = pos.X > Shell.ActualWidth / 2;
                    PanelDropped?.Invoke(this, dragged, after);
                }
                e.Handled = true;
                return;
            }

            var entries = new List<FileEntry>();

            if (e.Data.GetDataPresent("SuperExplorer.FileEntry"))
            {
                var entry = e.Data.GetData("SuperExplorer.FileEntry") as FileEntry;
                if (entry != null) entries.Add(entry);
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];

                // 从外部（系统资源管理器等）拖入的全是文件夹：交给上层决定「复制进去 / 作为面板打开」
                var dirs = new List<string>();
                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        try { if (Directory.Exists(p)) dirs.Add(p); }
                        catch { }
                    }
                }
                if (dirs.Count > 0 && dirs.Count == (paths == null ? 0 : paths.Length))
                {
                    ExternalFolderDropped?.Invoke(this, dirs);
                    e.Handled = true;
                    return;
                }

                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        var entry = FileSystemService.EntryFromPath(p);
                        if (entry != null) entries.Add(entry);
                    }
                }
            }

            if (entries.Count == 0) return;

            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            FilesDropped?.Invoke(this, entries, copy);
            e.Handled = true;
        }

        private bool CanAcceptFiles(IDataObject data)
        {
            if (!data.GetDataPresent("SuperExplorer.FileEntry") &&
                !data.GetDataPresent(DataFormats.FileDrop)) return false;

            var entry = data.GetData("SuperExplorer.FileEntry") as FileEntry;
            if (entry != null && string.IsNullOrEmpty(FolderPath)) return false;
            if (entry != null)
            {
                if (FileSystemService.SameFolder(Path.GetDirectoryName(entry.FullPath), FolderPath))
                    return false;
                if (entry.Kind == EntryKind.Folder &&
                    FileSystemService.IsSubPathOf(FolderPath, entry.FullPath)) return false;
            }
            return true;
        }
    }
}
