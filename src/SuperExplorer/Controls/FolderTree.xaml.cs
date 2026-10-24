using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SuperExplorer.Models;
using SuperExplorer.Services;

namespace SuperExplorer.Controls
{
    public partial class FolderTree : UserControl
    {
        public event Action<string> FolderOpenRequested;
        public event Action<string> ExplorerRequested;
        public event Action<string> Notified;
        public event Action<string, List<FileEntry>, bool> FilesDropped;
        public event Action FavoritesChanged;

        private readonly ObservableCollection<FolderNode> _visible = new ObservableCollection<FolderNode>();
        private readonly ObservableCollection<FolderNode> _favorites = new ObservableCollection<FolderNode>();

        private FolderNode _pc;
        private string _rootPath;
        private ListBoxItem _highlight;
        private bool _initialized;

        public FolderTree()
        {
            InitializeComponent();

            // 设置可能在控件构造之后才加载，等加载完成再应用
            Loaded += (s, e) =>
            {
                double h = SettingsService.Current.FavoritesHeight;
                if (h < 52 || double.IsNaN(h)) h = 150;
                FavRow.Height = new GridLength(h);
            };
        }

        private void FavSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            double h = Math.Max(52, FavRow.ActualHeight);
            SettingsService.Current.FavoritesHeight = h;
            SettingsService.Save();
        }

        private void FavSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            FavRow.Height = new GridLength(150);
            SettingsService.Current.FavoritesHeight = 150;
            SettingsService.Save();
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            TreeList.ItemsSource = _visible;
            FavList.ItemsSource = _favorites;

            _pc = new FolderNode
            {
                Name = "此电脑",
                FullPath = "",
                Kind = NodeKind.Pc,
                HasChildren = true,
                IsExpanded = true
            };
            foreach (var d in FileSystemService.GetDrives())
            {
                d.Parent = _pc;
                _pc.Children.Add(d);
            }
            _pc.ChildrenLoaded = true;

            _rootPath = SettingsService.Current.RootOverride;
            if (!string.IsNullOrEmpty(_rootPath) && !Directory.Exists(_rootPath))
            {
                _rootPath = null;
                SettingsService.Current.RootOverride = null;
            }

            RestoreExpanded();
            RebuildVisible();
            RefreshFavorites();
        }

        // ---------- 可见树 ----------

        private void RebuildVisible()
        {
            _visible.Clear();

            FolderNode start = _pc;
            if (!string.IsNullOrEmpty(_rootPath))
            {
                var rootNode = EnsureNode(_rootPath);
                if (rootNode != null) start = rootNode;
                else
                {
                    _rootPath = null;
                    SettingsService.Current.RootOverride = null;
                }
            }

            AddVisible(start, 0);

            var favs = new HashSet<string>(SettingsService.Current.Favorites, StringComparer.OrdinalIgnoreCase);
            foreach (var n in _visible)
            {
                if (n.Kind != NodeKind.Pc)
                    n.IsFavorite = favs.Contains(n.FullPath);
                // 容量统计已停用（实现保留在 FolderSizeService，需要时取消注释即可）
                // if (n.Kind == NodeKind.Folder)
                //     FolderSizeService.Request(n);
            }

            UpdateBanner();
        }

        private void AddVisible(FolderNode node, int depth)
        {
            node.Depth = depth;
            _visible.Add(node);

            if (node.IsExpanded && node.ChildrenLoaded)
            {
                foreach (var child in node.Children)
                    AddVisible(child, depth + 1);
            }
        }

        // ---------- 展开状态记忆 ----------

        private void RestoreExpanded()
        {
            var saved = SettingsService.Current.ExpandedPaths;
            if (saved == null || saved.Count == 0) return;

            // 浅层路径先展开，深层节点才会出现在树里
            foreach (var path in saved.Where(p => !string.IsNullOrEmpty(p)).OrderBy(p => p.Length))
            {
                var node = EnsureNode(path);
                if (node == null) continue;

                if (!node.ChildrenLoaded) LoadChildren(node);
                node.IsExpanded = node.Children.Count > 0;
            }
        }

        public void SaveExpandedState()
        {
            if (_pc == null) return;

            var list = new List<string>();
            CollectExpanded(_pc, list, 0);
            SettingsService.Current.ExpandedPaths = list;
            SettingsService.Save();
        }

        private static void CollectExpanded(FolderNode node, List<string> list, int depth)
        {
            if (node == null || depth > 40) return;

            if (node.Kind != NodeKind.Pc && node.IsExpanded && !string.IsNullOrEmpty(node.FullPath))
                list.Add(node.FullPath);

            if (!node.ChildrenLoaded) return;
            foreach (var child in node.Children)
                CollectExpanded(child, list, depth + 1);
        }

        private FolderNode EnsureNode(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string root;
            try { root = Path.GetPathRoot(path); }
            catch { return null; }
            if (string.IsNullOrEmpty(root)) return null;

            var drive = _pc.Children.FirstOrDefault(d =>
                string.Equals(d.FullPath.TrimEnd('\\'), root.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase));
            if (drive == null) return null;

            var current = drive;
            var rest = path.Substring(root.Length).TrimEnd('\\');
            if (rest.Length == 0) return current;

            foreach (var part in rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                LoadChildren(current);
                var next = current.Children.FirstOrDefault(c =>
                    string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        private void LoadChildren(FolderNode node)
        {
            if (node == null || node.ChildrenLoaded) return;

            node.Children.Clear();
            var kids = FileSystemService.GetChildFolders(node);
            foreach (var k in kids)
            {
                k.Parent = node;
                node.Children.Add(k);
            }
            node.ChildrenLoaded = true;
            node.HasChildren = node.Children.Count > 0;
        }

        private void ToggleNode(FolderNode node)
        {
            if (node == null) return;

            if (!node.ChildrenLoaded) LoadChildren(node);
            node.IsExpanded = !node.IsExpanded;
            if (node.IsExpanded && node.Children.Count == 0) node.IsExpanded = false;

            RebuildVisible();
            SaveExpandedState();
        }

        public void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var node = EnsureNode(path);
            if (node == null) return;

            var chain = new List<FolderNode>();
            var p = node.Parent;
            while (p != null)
            {
                chain.Add(p);
                p = p.Parent;
            }
            chain.Reverse();

            foreach (var n in chain)
            {
                if (!n.ChildrenLoaded) LoadChildren(n);
                n.IsExpanded = true;
            }
            RebuildVisible();

            var target = _visible.FirstOrDefault(v =>
                string.Equals(v.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                TreeList.SelectedItem = target;

                // 刚重建完可见列表就调用 ScrollIntoView，虚拟化容器还没生成，
                // 内部索引可能为 -1 抛 ArgumentOutOfRangeException(itemIndex)，延后一帧再滚动
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_visible.Contains(target)) TreeList.ScrollIntoView(target);
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            SaveExpandedState();
        }

        public void RefreshTree()
        {
            if (_pc == null) return;

            var expanded = _visible.Where(n => n.IsExpanded && n.Kind != NodeKind.Pc).ToList();
            foreach (var n in expanded)
            {
                n.ChildrenLoaded = false;
                LoadChildren(n);
            }
            RebuildVisible();
            RefreshSizes();
        }

        public void RefreshSizes()
        {
            if (_pc == null) return;

            // 磁盘容量是 DriveInfo 直接读取，开销可忽略，保留
            foreach (var d in _pc.Children)
                UpdateDriveSize(d);

            // 文件夹容量统计已停用：递归扫描磁盘开销大，需要时恢复以下代码即可
            // FolderSizeService.ClearCache();
            // foreach (var n in _visible)
            // {
            //     if (n.Kind != NodeKind.Folder) continue;
            //     n.Size = 0;
            //     FolderSizeService.Request(n);
            // }
            // foreach (var f in _favorites)
            // {
            //     if (f.Kind != NodeKind.Folder) continue;
            //     f.Size = 0;
            //     FolderSizeService.Request(f);
            // }
        }

        private static void UpdateDriveSize(FolderNode node)
        {
            try
            {
                var di = new DriveInfo(node.FullPath);
                if (!di.IsReady) return;
                node.TotalSize = di.TotalSize;
                node.Size = di.TotalSize - di.TotalFreeSpace;
                node.SizeRatio = di.TotalSize > 0 ? (double)node.Size / di.TotalSize : 0;
            }
            catch { }
        }

        // ---------- 常用文件夹 ----------

        public void RefreshFavorites()
        {
            _favorites.Clear();

            foreach (var p in SettingsService.Current.Favorites.ToList())
            {
                var node = EnsureNode(p);
                if (node == null)
                {
                    node = new FolderNode
                    {
                        Name = SafeName(p),
                        FullPath = p,
                        Kind = NodeKind.Folder
                    };
                }
                node.IsFavorite = true;
                _favorites.Add(node);
                // 容量统计已停用
                // FolderSizeService.Request(node);
            }

            FavEmpty.Visibility = _favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string SafeName(string path)
        {
            try { return Path.GetFileName(path.TrimEnd('\\')); }
            catch { return path; }
        }

        private void ToggleFavorite(FolderNode node)
        {
            if (node == null || node.Kind == NodeKind.Pc) return;

            var list = SettingsService.Current.Favorites;
            int idx = list.FindIndex(p =>
                string.Equals(p, node.FullPath, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0) list.RemoveAt(idx);
            else list.Add(node.FullPath);

            SettingsService.Save();
            node.IsFavorite = idx < 0;

            RefreshFavorites();
            RebuildVisible();
            FavoritesChanged?.Invoke();
        }

        private void Fav_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var node = (sender as FrameworkElement)?.DataContext as FolderNode;

            // 单击记录起点用于拖拽排序；双击才打开
            if (e.ClickCount == 1)
            {
                _favDragNode = node;
                _favDragStart = e.GetPosition(null);
                _favCanDrag = node != null && !string.IsNullOrEmpty(node.FullPath);
            }
            else
            {
                _favCanDrag = false;
            }

            if (e.ClickCount != 2) return;
            if (node != null && !string.IsNullOrEmpty(node.FullPath))
                FolderOpenRequested?.Invoke(node.FullPath);
        }

        // ---------- 常用文件夹拖拽排序 ----------

        private FolderNode _favDragNode;
        private Point _favDragStart;
        private bool _favCanDrag;
        private Border _favHighlight;

        private void FavRow_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_favCanDrag || _favDragNode == null || e.LeftButton != MouseButtonState.Pressed) return;

            var p = e.GetPosition(null);
            if (Math.Abs(p.X - _favDragStart.X) < 6 && Math.Abs(p.Y - _favDragStart.Y) < 6) return;

            _favCanDrag = false;
            try
            {
                var data = new DataObject("SuperExplorer.Favorite", _favDragNode.FullPath);
                DragDrop.DoDragDrop((FrameworkElement)sender, data, DragDropEffects.Move);
            }
            catch { }
            finally
            {
                ClearFavHighlight();
                _favDragNode = null;
            }
        }

        private bool IsFavoriteDrag(IDataObject data)
        {
            return data != null && data.GetDataPresent("SuperExplorer.Favorite");
        }

        private void HighlightFavRow(FrameworkElement row)
        {
            var border = row as Border;
            if (border == null) return;
            if (!ReferenceEquals(border, _favHighlight))
            {
                ClearFavHighlight();
                _favHighlight = border;
                border.Background = (Brush)Application.Current.Resources["AccentSoftBrush"];
            }
        }

        private void ClearFavHighlight()
        {
            if (_favHighlight == null) return;
            // 清掉本地值，恢复模板里悬停高亮的触发器
            _favHighlight.ClearValue(Border.BackgroundProperty);
            _favHighlight = null;
        }

        /// <summary>把常用项 src 移动到 target 的位置（插入到 target 前）。</summary>
        private void MoveFavoriteTo(string src, string target)
        {
            var list = SettingsService.Current.Favorites;
            int from = list.FindIndex(p => string.Equals(p, src, StringComparison.OrdinalIgnoreCase));
            int to = list.FindIndex(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase));
            if (from < 0 || to < 0 || from == to) return;

            var item = list[from];
            list.RemoveAt(from);
            if (from < to) to--;
            list.Insert(to, item);

            SettingsService.Save();
            RefreshFavorites();
        }

        private void MoveFavoriteToEnd(string src)
        {
            var list = SettingsService.Current.Favorites;
            int from = list.FindIndex(p => string.Equals(p, src, StringComparison.OrdinalIgnoreCase));
            if (from < 0 || from == list.Count - 1) return;

            var item = list[from];
            list.RemoveAt(from);
            list.Add(item);

            SettingsService.Save();
            RefreshFavorites();
        }

        private void FavArea_DragOver(object sender, DragEventArgs e)
        {
            // 只接管常用项自身的拖拽（排序）；文件拖入仍由各行自己的目标逻辑处理
            if (IsFavoriteDrag(e.Data))
            {
                ClearFavHighlight();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void FavArea_Drop(object sender, DragEventArgs e)
        {
            if (!IsFavoriteDrag(e.Data)) return;
            var src = e.Data.GetData("SuperExplorer.Favorite") as string;
            if (!string.IsNullOrEmpty(src)) MoveFavoriteToEnd(src);
            e.Handled = true;
        }

        private void FavRemove_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var node = (sender as FrameworkElement)?.DataContext as FolderNode;
            if (node == null) return;

            SettingsService.Current.Favorites.RemoveAll(p =>
                string.Equals(p, node.FullPath, StringComparison.OrdinalIgnoreCase));
            SettingsService.Save();
            node.IsFavorite = false;

            RefreshFavorites();
            RebuildVisible();
            FavoritesChanged?.Invoke();
        }

        // ---------- 树行操作 ----------

        private void Twist_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var node = (sender as FrameworkElement)?.DataContext as FolderNode;
            ToggleNode(node);
        }

        private void Fav_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var node = (sender as FrameworkElement)?.DataContext as FolderNode;
            ToggleFavorite(node);
        }

        private void Top_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var node = (sender as FrameworkElement)?.DataContext as FolderNode;
            if (node == null || string.IsNullOrEmpty(node.FullPath)) return;

            _rootPath = node.FullPath;
            SettingsService.Current.RootOverride = _rootPath;
            SettingsService.Save();

            RebuildVisible();
            Notified?.Invoke("已隐藏上级与同级，只显示 " + node.Name);
        }

        private void Explorer_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var node = (sender as FrameworkElement)?.DataContext as FolderNode;
            if (node == null || string.IsNullOrEmpty(node.FullPath)) return;
            ExplorerRequested?.Invoke(node.FullPath);
        }

        private void Unhide_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _rootPath = null;
            SettingsService.Current.RootOverride = null;
            SettingsService.Save();

            RebuildVisible();
            Notified?.Invoke("已恢复完整文件夹树");
        }

        private void UpdateBanner()
        {
            if (!string.IsNullOrEmpty(_rootPath))
            {
                RootBanner.Visibility = Visibility.Visible;
                RootText.Text = "已隐藏上级与同级：" + _rootPath;
            }
            else
            {
                RootBanner.Visibility = Visibility.Collapsed;
            }
        }

        private void Tree_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

            var node = TreeList.SelectedItem as FolderNode;
            if (node == null || string.IsNullOrEmpty(node.FullPath)) return;

            FolderOpenRequested?.Invoke(node.FullPath);
            e.Handled = true;
        }

        // ---------- 拖放 ----------

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T) return (T)d;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private List<FileEntry> GetEntries(IDataObject data)
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

        private bool CanDrop(IDataObject data, FolderNode target)
        {
            if (target == null || target.Kind == NodeKind.Pc) return false;

            var entries = GetEntries(data);
            if (entries.Count == 0) return false;

            foreach (var e in entries)
            {
                if (FileSystemService.SameFolder(Path.GetDirectoryName(e.FullPath), target.FullPath))
                    return false;
                if (e.Kind == EntryKind.Folder &&
                    FileSystemService.IsSubPathOf(target.FullPath, e.FullPath))
                    return false;
            }
            return true;
        }

        private ListBoxItem ItemUnder(DependencyObject source)
        {
            return ItemsControl.ContainerFromElement(TreeList, source) as ListBoxItem;
        }

        private void Tree_DragOver(object sender, DragEventArgs e)
        {
            var item = ItemUnder(e.OriginalSource as DependencyObject);
            var node = item == null ? null : item.DataContext as FolderNode;

            if (!CanDrop(e.Data, node))
            {
                ClearHighlight();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (!ReferenceEquals(item, _highlight))
            {
                ClearHighlight();
                _highlight = item;
                item.Background = (Brush)Application.Current.Resources["AccentSoftBrush"];
            }

            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            e.Handled = true;
        }

        private void Tree_DragLeave(object sender, DragEventArgs e)
        {
            if (TreeList.IsMouseOver) return;
            ClearHighlight();
        }

        private void Tree_Drop(object sender, DragEventArgs e)
        {
            var item = ItemUnder(e.OriginalSource as DependencyObject);
            var node = item == null ? null : item.DataContext as FolderNode;
            ClearHighlight();

            if (!CanDrop(e.Data, node)) return;

            var entries = GetEntries(e.Data);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            FilesDropped?.Invoke(node.FullPath, entries, copy);
            e.Handled = true;
        }

        private void Fav_DragOver(object sender, DragEventArgs e)
        {
            var border = sender as FrameworkElement;
            var node = border == null ? null : border.DataContext as FolderNode;

            // 常用项拖拽 = 排序：目标行高亮，拖到自身上无效
            if (IsFavoriteDrag(e.Data))
            {
                var src = e.Data.GetData("SuperExplorer.Favorite") as string;
                if (node != null && !string.Equals(node.FullPath, src, StringComparison.OrdinalIgnoreCase))
                {
                    HighlightFavRow(border);
                    e.Effects = DragDropEffects.Move;
                }
                else
                {
                    ClearFavHighlight();
                    e.Effects = DragDropEffects.None;
                }
                e.Handled = true;
                return;
            }

            if (!CanDrop(e.Data, node))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            e.Handled = true;
        }

        private void Fav_Drop(object sender, DragEventArgs e)
        {
            var border = sender as FrameworkElement;
            var node = border == null ? null : border.DataContext as FolderNode;

            if (IsFavoriteDrag(e.Data))
            {
                ClearFavHighlight();
                var src = e.Data.GetData("SuperExplorer.Favorite") as string;
                if (node != null && !string.IsNullOrEmpty(src) &&
                    !string.Equals(node.FullPath, src, StringComparison.OrdinalIgnoreCase))
                {
                    MoveFavoriteTo(src, node.FullPath);
                }
                e.Handled = true;
                return;
            }

            if (!CanDrop(e.Data, node)) return;

            var entries = GetEntries(e.Data);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            FilesDropped?.Invoke(node.FullPath, entries, copy);
            e.Handled = true;
        }

        private void ClearHighlight()
        {
            if (_highlight == null) return;
            try { _highlight.Background = Brushes.Transparent; } catch { }
            _highlight = null;
        }
    }
}
