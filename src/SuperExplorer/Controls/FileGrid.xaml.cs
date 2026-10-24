using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SuperExplorer.Models;

namespace SuperExplorer.Controls
{
    /// <summary>
    /// 只渲染可视区域行，避免一次性创建整目录的卡片与缩略图。
    /// 尺寸变化时只重排已有卡片，不清空重建，避免拖拽缩放时闪烁与空白卡片。
    /// 选择模型：单击选中；Ctrl+点击切换选中；Shift+点击范围选中；Ctrl+A 全选；点空白清空。
    /// </summary>
    public partial class FileGrid : UserControl
    {
        private const double Gap = 8;

        private List<FileEntry> _items = new List<FileEntry>();
        private readonly Dictionary<int, TileView> _tiles = new Dictionary<int, TileView>();

        private readonly HashSet<FileEntry> _selectionSet = new HashSet<FileEntry>();
        private int _anchor = -1;

        private double _tileSize = 140;
        private double _cellW = 140;
        private double _cellH = 176;
        private double _rowH = 184;
        private int _cols = 1;
        private int _totalRows;

        private int _lastFrom = -1;
        private int _lastTo = -1;
        private int _lastCols = -1;
        private double _lastCellW = -1;

        public event Action<FileEntry> EntryActivated;
        public event Action<IReadOnlyList<FileEntry>> EntriesActivated;
        public event Action<FileEntry> DeleteRequested;
        public event Action<FileEntry> LocateRequested;
        public event Action<FileEntry> SelectionChanged;
        public event Action<FileEntry, string> RenameRequested;
        public event Action NewFolderRequested;

        public FileGrid()
        {
            InitializeComponent();

            Scroll.ScrollChanged += (s, e) => UpdateWindow(false);
            SizeChanged += (s, e) => { MeasureCells(); UpdateWindow(true); };
            Loaded += (s, e) => { MeasureCells(); UpdateWindow(true); };

            // 点在空白处（不是任何卡片上）清空选择
            Scroll.PreviewMouseLeftButtonDown += OnBackgroundDown;
        }

        private void OnBackgroundDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<TileView>(e.OriginalSource as DependencyObject) == null)
                ClearSelection();
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

        // ---------- 选择 ----------

        public FileEntry SelectedEntry
        {
            get { return SelectionSnapshot().FirstOrDefault(); }
        }

        public IReadOnlyList<FileEntry> SelectedEntries
        {
            get { return SelectionSnapshot(); }
        }

        public int SelectedCount
        {
            get { return _selectionSet.Count; }
        }

        /// <summary>按目录顺序返回当前选中的条目快照。</summary>
        private List<FileEntry> SelectionSnapshot()
        {
            if (_selectionSet.Count == 0) return new List<FileEntry>();
            return _items.Where(_selectionSet.Contains).ToList();
        }

        public void SelectAll()
        {
            if (_items.Count == 0) return;
            foreach (var it in _items)
            {
                it.IsSelected = true;
                _selectionSet.Add(it);
            }
            SelectionChanged?.Invoke(SelectedEntry);
        }

        public void ClearSelection()
        {
            ClearSelectionQuiet();
            SelectionChanged?.Invoke(null);
        }

        private void ClearSelectionQuiet()
        {
            foreach (var it in _selectionSet) it.IsSelected = false;
            _selectionSet.Clear();
            _anchor = -1;
        }

        private void SelectSingle(FileEntry entry)
        {
            ClearSelectionQuiet();
            if (entry != null)
            {
                entry.IsSelected = true;
                _selectionSet.Add(entry);
                _anchor = _items.IndexOf(entry);
            }
            SelectionChanged?.Invoke(entry);
        }

        /// <summary>卡片按下时的选择逻辑，按当前键盘修饰键决定单选 / 切换 / 范围。</summary>
        private void HandleTileClick(FileEntry entry)
        {
            if (entry == null) return;

            var mods = Keyboard.Modifiers;
            int idx = _items.IndexOf(entry);

            if ((mods & ModifierKeys.Control) != 0)
            {
                if (_selectionSet.Contains(entry))
                {
                    entry.IsSelected = false;
                    _selectionSet.Remove(entry);
                }
                else
                {
                    entry.IsSelected = true;
                    _selectionSet.Add(entry);
                }
                _anchor = idx;
                SelectionChanged?.Invoke(SelectedEntry);
            }
            else if ((mods & ModifierKeys.Shift) != 0 && _anchor >= 0 && idx >= 0)
            {
                int from = Math.Min(_anchor, idx);
                int to = Math.Max(_anchor, idx);
                for (int i = from; i <= to; i++)
                {
                    var it = _items[i];
                    if (_selectionSet.Add(it)) it.IsSelected = true;
                }
                SelectionChanged?.Invoke(SelectedEntry);
            }
            else
            {
                SelectSingle(entry);
            }
        }

        // ---------- 对外接口 ----------

        public double TileSize
        {
            get { return _tileSize; }
            set
            {
                if (Math.Abs(_tileSize - value) < 0.5) return;
                _tileSize = value;
                MeasureCells();
                UpdateWindow(true);
            }
        }

        public void SetItems(List<FileEntry> items, bool keepScroll = false)
        {
            double offset = Scroll.VerticalOffset;

            _items = items ?? new List<FileEntry>();
            ClearSelectionQuiet();
            ClearTiles();
            _lastFrom = -1;
            _lastTo = -1;

            if (!keepScroll)
            {
                Scroll.ScrollToTop();
                Scroll.ScrollToHorizontalOffset(0);
            }

            MeasureCells();
            UpdateWindow(true);

            if (keepScroll && offset > 0)
                Scroll.ScrollToVerticalOffset(offset);

            SelectionChanged?.Invoke(null);
        }

        /// <summary>
        /// 字号或主题变化后重新度量并重排，卡片对象复用。
        /// </summary>
        public void RefreshMetrics()
        {
            _lastFrom = -1;
            _lastTo = -1;
            MeasureCells();
            UpdateWindow(true);
        }

        public void SelectByPath(string path)
        {
            var entry = _items.FirstOrDefault(x =>
                string.Equals(x.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (entry != null) SelectSingle(entry);
        }

        /// <summary>
        /// 进入重命名：只作用于当前唯一/首个选中项，先确保卡片可见。
        /// </summary>
        public void BeginRename()
        {
            var target = SelectedEntry;
            if (target == null) return;

            int idx = _items.IndexOf(target);
            if (idx < 0) return;

            double top = (idx / _cols) * _rowH;
            double bottom = top + _cellH;
            double viewTop = Scroll.VerticalOffset;
            double viewBottom = viewTop + Scroll.ViewportHeight;

            if (top < viewTop) Scroll.ScrollToVerticalOffset(top);
            else if (bottom > viewBottom) Scroll.ScrollToVerticalOffset(bottom - Scroll.ViewportHeight);

            UpdateWindow(true);
            target.IsRenaming = true;
        }

        private void MenuNewFolder_Click(object sender, RoutedEventArgs e)
        {
            NewFolderRequested?.Invoke();
        }

        // ---------- 度量与虚拟化 ----------

        private void MeasureCells()
        {
            // 始终预留竖向滚动条宽度：滚动条出现/消失不再改变列数，
            // 否则拖分隔条时列数会在临界点反复跳变，导致无限重排。
            double reserve = SystemParameters.VerticalScrollBarWidth + 4;
            double avail = ActualWidth - reserve;
            if (avail < 60) avail = Math.Max(60, ActualWidth - 12);

            int cols = Math.Max(1, (int)Math.Floor((avail + Gap) / (_tileSize + Gap)));
            double w = Math.Floor((avail - Gap * (cols - 1)) / cols);
            if (w < 60)
            {
                cols = 1;
                w = Math.Max(60, avail);
            }

            _cols = cols;
            _cellW = w;

            // 卡片下方的名称与副标题按实际字体度量，字号调大后行高必须同步增长，否则第二行会被裁掉
            double nameH = TextLineHeight("FontSm", w);
            double metaH = TextLineHeight("FontXs", w);
            _cellH = Math.Ceiling(w + nameH + 3 + metaH + 1 + 4);
            _rowH = _cellH + Gap;
            _totalRows = (_items.Count + _cols - 1) / _cols;

            Spacer.Height = _totalRows > 0 ? _totalRows * _rowH - Gap : 0;
            Host.Width = _cols * _cellW + (_cols - 1) * Gap;
            Host.Height = Spacer.Height;
        }

        private static double TextLineHeight(string fontKey, double width)
        {
            double fs = 10;
            try { fs = (double)Application.Current.Resources[fontKey]; }
            catch { }

            var probe = new TextBlock { FontSize = fs, Text = "文件夹Ag" };
            probe.Measure(new Size(Math.Max(24, width - 2), double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }

        private void UpdateWindow(bool force)
        {
            if (_items.Count == 0)
            {
                if (force) ClearTiles();
                _lastFrom = -1;
                _lastTo = -1;
                return;
            }

            double offset = Scroll.VerticalOffset;
            double maxOffset = Math.Max(0, _totalRows * _rowH - Gap - Scroll.ViewportHeight);
            if (offset > maxOffset) offset = maxOffset;

            int startRow = Math.Max(0, (int)(offset / _rowH) - 1);
            int visibleRows = (int)Math.Ceiling(Scroll.ViewportHeight / _rowH) + 3;
            int endRow = Math.Min(_totalRows, startRow + visibleRows);

            int from = startRow * _cols;
            int to = Math.Min(_items.Count, endRow * _cols);

            bool sameWindow = from == _lastFrom && to == _lastTo;
            bool sameLayout = Math.Abs(_lastCellW - _cellW) < 0.5 && _lastCols == _cols;
            if (!force && sameWindow && sameLayout) return;

            _lastFrom = from;
            _lastTo = to;
            _lastCellW = _cellW;
            _lastCols = _cols;

            // 移出可视范围的卡片先放进复用池，滚回来时直接换绑数据，不再重新创建模板
            var pool = new List<TileView>();
            foreach (var k in _tiles.Keys.Where(k => k < from || k >= to).ToList())
            {
                pool.Add(_tiles[k]);
                _tiles.Remove(k);
            }

            for (int i = from; i < to; i++)
            {
                TileView tile;
                if (_tiles.TryGetValue(i, out tile) && ReferenceEquals(tile.DataContext, _items[i]))
                {
                    // 位置和内容都没变，保留原卡片
                }
                else
                {
                    if (tile != null)
                    {
                        _tiles.Remove(i);
                        pool.Add(tile);
                        tile = null;
                    }

                    if (pool.Count > 0)
                    {
                        tile = pool[0];
                        pool.RemoveAt(0);
                        tile.DataContext = _items[i];
                    }
                    else
                    {
                        tile = CreateTile(i);
                        Host.Children.Add(tile);
                    }

                    _tiles[i] = tile;
                }

                tile.Width = _cellW;
                tile.Height = _cellH;
                Canvas.SetLeft(tile, (i % _cols) * (_cellW + Gap));
                Canvas.SetTop(tile, (i / _cols) * _rowH);
            }

            foreach (var extra in pool)
            {
                Host.Children.Remove(extra);
            }
        }

        private TileView CreateTile(int index)
        {
            var tile = new TileView { DataContext = _items[index] };
            tile.SetBinding(TileView.IsSelectedProperty, new Binding("IsSelected"));

            tile.Selected += (s, e) =>
            {
                var entry = tile.DataContext as FileEntry;
                if (entry != null && !entry.IsRenaming) HandleTileClick(entry);
            };
            tile.OpenRequested += (s, e) =>
            {
                var entry = tile.DataContext as FileEntry;
                if (entry == null) return;

                // 多选状态下双击其中任意一个 = 打开整组（文件夹开面板、文件开系统程序）
                if (_selectionSet.Count > 1 && _selectionSet.Contains(entry))
                    EntriesActivated?.Invoke(SelectionSnapshot());
                else
                    EntryActivated?.Invoke(entry);
            };
            tile.DragSourceRequested += t => StartTileDrag(t);
            tile.DeleteRequested += (s, e) =>
            {
                var entry = tile.DataContext as FileEntry;
                if (entry != null) DeleteRequested?.Invoke(entry);
            };
            tile.LocateRequested += (s, e) =>
            {
                var entry = tile.DataContext as FileEntry;
                if (entry != null) LocateRequested?.Invoke(entry);
            };
            tile.RenameCommitted += (entry, name) => RenameRequested?.Invoke(entry, name);
            tile.NewFolderRequested += () => NewFolderRequested?.Invoke();

            return tile;
        }

        /// <summary>
        /// 多选拖拽：拖动选中组中的任意卡片 = 拖整组；拖未选中的卡片 = 只拖它一个。
        /// </summary>
        private void StartTileDrag(TileView tile)
        {
            var anchor = tile.DataContext as FileEntry;
            if (anchor == null) return;

            var list = (_selectionSet.Count > 1 && _selectionSet.Contains(anchor))
                ? SelectionSnapshot()
                : new List<FileEntry> { anchor };

            try
            {
                var data = new DataObject();
                data.SetData("SuperExplorer.FileEntries", list);
                // 同时提供 FileDrop，方便直接拖进系统资源管理器 / 其他软件
                data.SetData(DataFormats.FileDrop, list.Select(x => x.FullPath).ToArray());
                DragDrop.DoDragDrop(tile, data, DragDropEffects.Move | DragDropEffects.Copy);
            }
            catch { }
        }

        private void ClearTiles()
        {
            Host.Children.Clear();
            _tiles.Clear();
        }
    }
}
