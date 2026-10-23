using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SuperExplorer.Controls;
using SuperExplorer.Models;
using SuperExplorer.Services;
using UndoAction = SuperExplorer.Models.UndoAction;

namespace SuperExplorer
{
    public partial class MainWindow : Window
    {
        private const int MaxPanels = 6;

        private readonly List<FolderPanel> _panels = new List<FolderPanel>();
        private FolderPanel _active;

        private string _layout = "row";
        private double _tileSize = 140;
        private List<double> _rowWeights = new List<double>();

        private readonly List<FileEntry> _clipboard = new List<FileEntry>();
        private bool _ready;
        private bool _busy;
        private bool _relayoutQueued;

        public MainWindow()
        {
            InitializeComponent();

            SettingsService.Load();
            ThemeService.Apply(SettingsService.Current.Theme);
            UiScaleService.Apply(SettingsService.Current.FontStep);

            ApplyUiFromSettings();

            Tree.Initialize();
            Tree.FolderOpenRequested += OpenFolder;
            Tree.ExplorerRequested += FileSystemService.OpenInExplorer;
            Tree.Notified += Toast;
            Tree.FilesDropped += (path, entries, copy) => { _ = TransferAsync(entries, path, copy); };
            Tree.FavoritesChanged += RefreshPanelFavorites;

            RestorePanels();
            BuildWorkspace();
            UpdateStatus();

            PreviewKeyDown += OnKeyDown;
            Closing += (s, e) =>
            {
                Tree.SaveExpandedState();
                SaveOpenFolders();
            };

            SettingsPopup.Opened += (s, e) =>
            {
                BuildTicks(SizeTicks, SizeSlider);
                BuildTicks(FontTicks, FontSlider);
            };
            SizeTicks.SizeChanged += (s, e) => BuildTicks(SizeTicks, SizeSlider);
            FontTicks.SizeChanged += (s, e) => BuildTicks(FontTicks, FontSlider);

            // 工作区尺寸变化后所有面板重新度量，避免旧卡片残留或错位
            WorkspaceHost.SizeChanged += (s, e) => QueueRelayoutAll();

            _ready = true;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            }
            catch { }
        }

        // ---------- 初始化 ----------

        private void ApplyUiFromSettings()
        {
            var s = SettingsService.Current;

            _layout = s.Layout == "grid" ? "grid" : "row";
            _tileSize = s.TileSize >= 80 && s.TileSize <= 400 ? s.TileSize : 140;

            SizeSlider.Value = _tileSize;
            SizeValueText.Text = (int)_tileSize + " px";

            int fontStep = Math.Max(0, Math.Min(UiScaleService.MaxStep, s.FontStep));
            FontSlider.Value = fontStep;
            FontValueText.Text = (13 + fontStep) + " px";

            RowLayoutBtn.IsChecked = _layout == "row";
            GridLayoutBtn.IsChecked = _layout == "grid";
            ConfirmDeleteCheck.IsChecked = s.ConfirmDelete;

            double treeWidth = s.TreeWidth >= 150 && s.TreeWidth <= 600 ? s.TreeWidth : 232;
            TreeColumn.Width = new GridLength(treeWidth);
            if (TreeWidthValueText != null) TreeWidthValueText.Text = (int)treeWidth + " px";

            switch ((s.Theme ?? "Pink").ToLowerInvariant())
            {
                case "violet": ThemeViolet.IsChecked = true; break;
                case "cyan": ThemeCyan.IsChecked = true; break;
                case "green": ThemeGreen.IsChecked = true; break;
                case "amber": ThemeAmber.IsChecked = true; break;
                case "mono": ThemeMono.IsChecked = true; break;
                default: ThemePink.IsChecked = true; break;
            }
        }

        private void RestorePanels()
        {
            foreach (var path in SettingsService.Current.OpenFolders)
            {
                if (_panels.Count >= MaxPanels) break;
                if (!Directory.Exists(path)) continue;
                if (_panels.Any(p => FileSystemService.SameFolder(p.FolderPath, path))) continue;
                _panels.Add(CreatePanel(path));
            }

            if (_panels.Count > 0) SetActive(_panels[0]);
        }

        private FolderPanel CreatePanel(string path)
        {
            var panel = new FolderPanel();
            panel.Load(path, _tileSize);

            panel.CloseRequested += OnPanelClose;
            panel.Activated += SetActive;
            panel.Navigated += p =>
            {
                SaveOpenFolders();
                Tree.SelectPath(p.FolderPath);
            };
            panel.FolderActivated += (p, childPath) => OpenFolder(childPath);
            panel.FileActivated += (p, entry) => FileSystemService.OpenFile(entry.FullPath);
            panel.DeleteRequested += (p, entry) => DeleteEntry(p, entry);
            panel.LocateRequested += (p, entry) => FileSystemService.LocateInExplorer(entry.FullPath);
            panel.FilesDropped += (p, entries, copy) => { _ = TransferAsync(entries, p.FolderPath, copy); };
            panel.PanelDropped += ReorderPanels;
            panel.Notified += Toast;
            panel.FavoriteToggled += p =>
            {
                Tree.RefreshFavorites();
                RefreshPanelFavorites();
            };

            return panel;
        }

        // ---------- 面板 ----------

        private void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                Toast("文件夹不存在：" + path);
                return;
            }

            var existing = _panels.FirstOrDefault(p => FileSystemService.SameFolder(p.FolderPath, path));
            if (existing != null)
            {
                SetActive(existing);
                Tree.SelectPath(path);
                return;
            }

            if (_panels.Count >= MaxPanels)
            {
                Toast("最多同时打开 " + MaxPanels + " 个文件夹");
                return;
            }

            var panel = CreatePanel(path);
            _panels.Add(panel);
            BuildWorkspace();
            SetActive(panel);
            SaveOpenFolders();
            Tree.SelectPath(path);
        }

        private void OnPanelClose(FolderPanel panel)
        {
            _panels.Remove(panel);
            if (ReferenceEquals(_active, panel)) _active = null;

            BuildWorkspace();
            SaveOpenFolders();

            if (_active == null && _panels.Count > 0) SetActive(_panels[0]);
            UpdateStatus();
        }

        private void SetActive(FolderPanel panel)
        {
            _active = panel;
            foreach (var p in _panels)
                p.IsActive = ReferenceEquals(p, panel);
        }

        private void ReorderPanels(FolderPanel target, FolderPanel dragged, bool after)
        {
            int from = _panels.IndexOf(dragged);
            int ti = _panels.IndexOf(target);
            if (from < 0 || ti < 0 || from == ti) return;

            _panels.RemoveAt(from);
            if (from < ti) ti--;
            int insert = after ? ti + 1 : ti;
            if (insert < 0) insert = 0;
            if (insert > _panels.Count) insert = _panels.Count;
            _panels.Insert(insert, dragged);

            BuildWorkspace();
            SaveOpenFolders();
        }

        private void SaveOpenFolders()
        {
            SettingsService.Current.OpenFolders = _panels.Select(p => p.FolderPath).ToList();
            SettingsService.Save();
        }

        // ---------- 工作区布局 ----------

        private void BuildWorkspace()
        {
            // 面板可能挂在上一轮布局生成的嵌套列容器里，必须先断开，否则重新加入会报「已是另一个元素的逻辑子元素」
            foreach (var panel in _panels)
            {
                if (panel.Parent is Panel parent) parent.Children.Remove(panel);
            }

            WorkspaceHost.Children.Clear();
            WorkspaceHost.RowDefinitions.Clear();
            WorkspaceHost.ColumnDefinitions.Clear();

            if (_panels.Count == 0)
            {
                EmptyHint.Visibility = Visibility.Visible;
                UpdateStatus();
                return;
            }

            EmptyHint.Visibility = Visibility.Collapsed;

            if (_layout == "grid") BuildGridLayout();
            else BuildRowLayout();

            QueueRelayoutAll();
            UpdateStatus();
        }

        private void QueueRelayoutAll()
        {
            if (_relayoutQueued) return;
            _relayoutQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _relayoutQueued = false;
                foreach (var p in _panels) p.RefreshLayout();
            }), DispatcherPriority.Loaded);
        }

        private void BuildRowLayout()
        {
            EnsureRowWeights();

            for (int i = 0; i < _panels.Count; i++)
            {
                WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(_rowWeights[i], GridUnitType.Star),
                    MinWidth = 240
                });

                var panel = _panels[i];
                panel.MinWidth = 240;
                panel.MinHeight = 160;
                Grid.SetColumn(panel, i * 2);
                Grid.SetRow(panel, 0);
                WorkspaceHost.Children.Add(panel);

                if (i < _panels.Count - 1)
                {
                    var splitter = new GridSplitter
                    {
                        ResizeDirection = GridResizeDirection.Columns,
                        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        Width = 8
                    };
                    splitter.DragCompleted += RowSplitter_DragCompleted;
                    WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    Grid.SetColumn(splitter, i * 2 + 1);
                    Grid.SetRow(splitter, 0);
                    WorkspaceHost.Children.Add(splitter);
                }
            }
        }

        private void EnsureRowWeights()
        {
            var s = SettingsService.Current;
            if (s.PanelWeights == null) s.PanelWeights = new List<double>();
            if (s.PanelWeights.Count != _panels.Count || s.PanelWeights.Any(w => w <= 0))
                s.PanelWeights = Enumerable.Repeat(1.0, _panels.Count).ToList();
            _rowWeights = s.PanelWeights;
        }

        private void RowSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            var weights = new List<double>();
            for (int i = 0; i < _panels.Count; i++)
            {
                var cd = WorkspaceHost.ColumnDefinitions[i * 2];
                double w = Math.Max(1, cd.ActualWidth);
                weights.Add(w);
                cd.Width = new GridLength(w, GridUnitType.Star);
            }
            SettingsService.Current.PanelWeights = weights;
            SettingsService.Save();
        }

        /// <summary>
        /// 两列布局：左列 ceil(n/2) 个、右列 floor(n/2) 个。
        /// 奇数个时右列面板更少，单个自然占满整列高度；偶数个时两列数量相同，就是常规网格。
        /// </summary>
        private void BuildGridLayout()
        {
            int n = _panels.Count;
            if (n == 0) return;

            if (n == 1)
            {
                var only = _panels[0];
                only.MinWidth = 220;
                only.MinHeight = 170;
                WorkspaceHost.Children.Add(only);
                return;
            }

            int leftCount = (n + 1) / 2;
            int rightCount = n / 2;

            var colWeights = EnsureWeights(SettingsService.Current.GridColumnWeights, 2);
            SettingsService.Current.GridColumnWeights = colWeights;

            WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(colWeights[0], GridUnitType.Star),
                MinWidth = 220
            });
            WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(colWeights[1], GridUnitType.Star),
                MinWidth = 220
            });

            var colSplitter = new GridSplitter
            {
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = 8
            };
            colSplitter.DragCompleted += GridColumnSplitter_DragCompleted;
            Grid.SetColumn(colSplitter, 1);
            WorkspaceHost.Children.Add(colSplitter);

            var leftHost = BuildGridColumn(_panels.Take(leftCount).ToList(),
                SettingsService.Current.GridLeftRowWeights,
                w => SettingsService.Current.GridLeftRowWeights = w);
            Grid.SetColumn(leftHost, 0);
            WorkspaceHost.Children.Add(leftHost);

            var rightHost = BuildGridColumn(_panels.Skip(leftCount).ToList(),
                SettingsService.Current.GridRightRowWeights,
                w => SettingsService.Current.GridRightRowWeights = w);
            Grid.SetColumn(rightHost, 2);
            WorkspaceHost.Children.Add(rightHost);
        }

        private Grid BuildGridColumn(List<FolderPanel> panels, List<double> stored, Action<List<double>> save)
        {
            var host = new Grid();
            var weights = EnsureWeights(stored, panels.Count);
            save(weights);

            for (int i = 0; i < panels.Count; i++)
            {
                host.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(weights[i], GridUnitType.Star),
                    MinHeight = 170
                });
                if (i < panels.Count - 1)
                    host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            }

            for (int i = 0; i < panels.Count; i++)
            {
                var panel = panels[i];
                panel.MinWidth = 220;
                panel.MinHeight = 170;
                Grid.SetRow(panel, i * 2);
                host.Children.Add(panel);
            }

            for (int i = 0; i < panels.Count - 1; i++)
            {
                var splitter = new GridSplitter
                {
                    ResizeDirection = GridResizeDirection.Rows,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Height = 8
                };
                splitter.DragCompleted += (s, e) =>
                {
                    var ws = new List<double>();
                    for (int r = 0; r < panels.Count; r++)
                    {
                        double h = Math.Max(1, host.RowDefinitions[r * 2].ActualHeight);
                        ws.Add(h);
                        host.RowDefinitions[r * 2].Height = new GridLength(h, GridUnitType.Star);
                    }
                    save(ws);
                    SettingsService.Save();
                };
                Grid.SetRow(splitter, i * 2 + 1);
                host.Children.Add(splitter);
            }

            return host;
        }

        private static List<double> EnsureWeights(List<double> current, int count)
        {
            if (current == null || current.Count != count || current.Any(w => w <= 0))
                return Enumerable.Repeat(1.0, count).ToList();
            return current;
        }

        private void GridColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            var weights = new List<double>();
            foreach (int c in new[] { 0, 2 })
            {
                var cd = WorkspaceHost.ColumnDefinitions[c];
                double w = Math.Max(1, cd.ActualWidth);
                weights.Add(w);
                cd.Width = new GridLength(w, GridUnitType.Star);
            }
            SettingsService.Current.GridColumnWeights = weights;
            SettingsService.Save();
        }

        // ---------- 顶栏 ----------

        private void Gear_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
        }

        private void TreeSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            double w = Math.Max(150, TreeColumn.ActualWidth);
            SettingsService.Current.TreeWidth = w;
            SettingsService.Save();
            if (TreeWidthValueText != null) TreeWidthValueText.Text = (int)w + " px";
        }

        private void ResetTreeWidth_Click(object sender, RoutedEventArgs e)
        {
            TreeColumn.Width = new GridLength(232);
            SettingsService.Current.TreeWidth = 232;
            SettingsService.Save();
            if (TreeWidthValueText != null) TreeWidthValueText.Text = "232 px";
            Toast("左侧栏宽度已恢复默认");
        }

        private void RefreshPanelFavorites()
        {
            foreach (var p in _panels) p.RefreshFavoriteState();
        }

        private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SizeValueText != null) SizeValueText.Text = (int)e.NewValue + " px";
            if (!_ready) return;
            _tileSize = e.NewValue;
            foreach (var p in _panels) p.TileSize = _tileSize;
            SettingsService.Current.TileSize = _tileSize;
            SettingsService.Save();
        }

        private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int step = (int)Math.Round(e.NewValue);
            if (FontValueText != null) FontValueText.Text = (13 + step) + " px";
            if (!_ready) return;

            UiScaleService.Apply(step);
            SettingsService.Current.FontStep = step;
            SettingsService.Save();

            foreach (var p in _panels) p.RefreshLayout();
        }

        /// <summary>
        /// 在滑块下方按档位画粗点，点中心与滑块手柄中心对齐。
        /// </summary>
        private static void BuildTicks(Canvas canvas, Slider slider)
        {
            if (canvas == null || slider == null) return;
            canvas.Children.Clear();

            double width = canvas.ActualWidth;
            if (width <= 1) return;

            double step = slider.TickFrequency;
            if (step <= 0) return;

            int count = (int)Math.Round((slider.Maximum - slider.Minimum) / step);
            if (count < 1) return;

            const double thumb = 14;
            double span = Math.Max(1, width - thumb);
            var brush = (Brush)Application.Current.Resources["DimBrush"];

            for (int i = 0; i <= count; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = brush
                };
                Canvas.SetLeft(dot, thumb / 2 + span * i / count - 2);
                Canvas.SetTop(dot, 1.5);
                canvas.Children.Add(dot);
            }
        }

        private void Layout_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var rb = sender as RadioButton;
            if (rb == null) return;

            _layout = (rb.Tag as string) == "grid" ? "grid" : "row";
            SettingsService.Current.Layout = _layout;
            SettingsService.Save();
            BuildWorkspace();
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var rb = sender as RadioButton;
            if (rb == null) return;

            var name = rb.Tag as string;
            ThemeService.Apply(name);
            SettingsService.Current.Theme = name;
            SettingsService.Save();

            foreach (var p in _panels) p.RefreshChrome();
            BuildTicks(SizeTicks, SizeSlider);
            BuildTicks(FontTicks, FontSlider);
        }

        private void ConfirmDelete_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            bool on = ConfirmDeleteCheck.IsChecked == true;
            SettingsService.Current.ConfirmDelete = on;
            SettingsService.Save();
            Toast(on ? "删除前会弹出确认" : "删除将直接执行（仍进回收站）");
        }

        // ---------- 快捷键 ----------

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 正在卡片上重命名时，按键交给输入框处理
            if (Keyboard.FocusedElement is TextBox) return;

            if (e.Key == Key.Escape)
            {
                if (SettingsPopup.IsOpen) SettingsPopup.IsOpen = false;
                return;
            }

            if (e.Key == Key.F2)
            {
                e.Handled = true;
                if (_active != null) _active.BeginRename();
                return;
            }

            if (e.Key == Key.Delete)
            {
                e.Handled = true;
                DeleteSelected();
                return;
            }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!ctrl) return;

            if (e.Key == Key.Z)
            {
                e.Handled = true;
                _ = UndoAsync();
            }
            else if (e.Key == Key.C)
            {
                e.Handled = true;
                CopySelection();
            }
            else if (e.Key == Key.V)
            {
                e.Handled = true;
                PasteIntoActive();
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                DeleteSelected();
            }
        }

        private void CopySelection()
        {
            var entry = _active == null ? null : _active.SelectedEntry;
            if (entry == null)
            {
                Toast("先在面板里选中一个文件");
                return;
            }
            _clipboard.Clear();
            _clipboard.Add(entry);

            // 同时写入系统剪贴板：文件路径让资源管理器能粘贴，图片内容让聊天/绘图软件能直接粘贴
            try
            {
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, new[] { entry.FullPath });

                if (entry.Kind == EntryKind.Image)
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(entry.FullPath);
                    bmp.EndInit();
                    bmp.Freeze();
                    data.SetImage(bmp);
                }

                Clipboard.SetDataObject(data, true);
                Toast("已复制：" + entry.Name);
            }
            catch (Exception ex)
            {
                Toast("已复制到程序内，写入系统剪贴板失败：" + ex.Message);
            }
        }

        private void PasteIntoActive()
        {
            if (_clipboard.Count == 0)
            {
                Toast("剪贴板是空的");
                return;
            }
            if (_active == null)
            {
                Toast("先打开一个文件夹面板");
                return;
            }
            _ = TransferAsync(_clipboard.ToList(), _active.FolderPath, true);
        }

        private void DeleteSelected()
        {
            if (_active == null) return;
            var entry = _active.SelectedEntry;
            if (entry == null)
            {
                Toast("先在面板里选中一个文件");
                return;
            }
            DeleteEntry(_active, entry);
        }

        // ---------- 删除 ----------

        private async void DeleteEntry(FolderPanel panel, FileEntry entry)
        {
            if (entry == null) return;

            if (SettingsService.Current.ConfirmDelete)
            {
                var dialog = new ConfirmDialog(
                    "本次删除将在本地执行，文件会被移入回收站。\n是否继续？",
                    entry.FullPath) { Owner = this };
                if (dialog.ShowDialog() != true) return;
            }

            bool ok = await Task.Run(() => FileSystemService.DeleteToRecycleBin(entry.FullPath));

            if (ok)
            {
                UndoService.Push(new UndoAction
                {
                    Kind = UndoKind.Delete,
                    SourcePath = entry.FullPath
                });
                Toast("已移入回收站：" + entry.Name);
                panel.Refresh();
                Tree.RefreshSizes();
                UpdateStatus();
            }
            else
            {
                Toast("删除失败：" + entry.Name);
            }
        }

        // ---------- 移动 / 复制 ----------

        private async Task TransferAsync(List<FileEntry> entries, string destDir, bool copy)
        {
            if (entries == null || entries.Count == 0) return;

            if (_busy)
            {
                Toast("正在处理上一个操作，请稍候");
                return;
            }

            if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
            {
                Toast("目标文件夹不存在：" + destDir);
                return;
            }

            _busy = true;
            try
            {
                foreach (var entry in entries)
                {
                    if (!File.Exists(entry.FullPath) && !Directory.Exists(entry.FullPath))
                    {
                        Toast("文件已不存在：" + entry.Name);
                        continue;
                    }

                    if (FileSystemService.SameFolder(Path.GetDirectoryName(entry.FullPath), destDir))
                    {
                        Toast("目标就是当前所在文件夹：" + entry.Name);
                        continue;
                    }

                    if (entry.Kind == EntryKind.Folder &&
                        FileSystemService.IsSubPathOf(destDir, entry.FullPath))
                    {
                        Toast("不能把文件夹移动到它自己的子目录");
                        continue;
                    }

                    if (copy)
                    {
                        try
                        {
                            var newPath = await Task.Run(() =>
                                FileSystemService.CopyItem(entry.FullPath, destDir));
                            UndoService.Push(new UndoAction
                            {
                                Kind = UndoKind.Copy,
                                SourcePath = entry.FullPath,
                                TargetPath = newPath
                            });
                            Toast(entry.Name + " 复制成功");
                        }
                        catch (Exception ex)
                        {
                            Toast(entry.Name + " 复制失败：" + ex.Message);
                        }
                        continue;
                    }

                    bool crossVolume = !FileSystemService.SameVolume(entry.FullPath, destDir);
                    if (crossVolume)
                        Toast(entry.Name + " 正在进行移动…");

                    try
                    {
                        var newPath = await Task.Run(() =>
                            FileSystemService.MoveItem(entry.FullPath, destDir));
                        UndoService.Push(new UndoAction
                        {
                            Kind = UndoKind.Move,
                            SourcePath = entry.FullPath,
                            TargetPath = newPath
                        });
                        Toast(crossVolume
                            ? entry.Name + " 移动成功"
                            : entry.Name + " 已移动");
                    }
                    catch (Exception ex)
                    {
                        Toast(entry.Name + " 移动失败：" + ex.Message);
                    }
                }
            }
            finally
            {
                _busy = false;
                RefreshAffected(destDir, entries);
                UpdateStatus();
            }
        }

        private void RefreshAffected(string destDir, List<FileEntry> entries)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destDir };
            foreach (var e in entries)
            {
                var d = Path.GetDirectoryName(e.FullPath);
                if (!string.IsNullOrEmpty(d)) dirs.Add(d);
            }

            foreach (var p in _panels)
            {
                if (dirs.Any(d => FileSystemService.SameFolder(d, p.FolderPath)))
                    p.Refresh();
            }

            Tree.RefreshTree();
        }

        // ---------- 撤销 ----------

        private async Task UndoAsync()
        {
            var result = await UndoService.UndoLastAsync();

            if (result.Ok)
            {
                Toast(result.Message);
                foreach (var p in _panels) p.Refresh();
                Tree.RefreshTree();
            }
            else
            {
                Toast("撤销失败：" + result.Message);
            }
            UpdateStatus();
        }

        // ---------- 状态与提示 ----------

        private void UpdateStatus()
        {
            OpenCountText.Text = "已打开 " + _panels.Count + " 个";
            StatusLeft.Text = _panels.Count + " 个面板 · 撤销栈 " + UndoService.Count + " 步";
        }

        public void Toast(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var border = new Border
            {
                Background = (Brush)Application.Current.Resources["Panel3Brush"],
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Opacity = 0
            };
            border.Child = new TextBlock
            {
                Text = message,
                FontSize = (double)Application.Current.Resources["FontMd"],
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620
            };

            ToastLayer.Children.Add(border);
            while (ToastLayer.Children.Count > 4) ToastLayer.Children.RemoveAt(0);

            border.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                ToastLayer.Children.Remove(border);
            };
            timer.Start();
        }
    }
}
