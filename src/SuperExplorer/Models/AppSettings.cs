using System.Collections.Generic;

namespace SuperExplorer.Models
{
    public class AppSettings
    {
        public string Theme { get; set; } = "Mono";
        public string Layout { get; set; } = "row";
        public double TileSize { get; set; } = 140;
        public int FontStep { get; set; }
        public bool ConfirmDelete { get; set; } = true;

        // 拖入文件夹：ask=弹窗询问 open=作为面板打开 copy=复制到当前文件夹
        public string FolderDropAction { get; set; } = "ask";

        // 启动时（接管开启且资源管理器有文件夹窗口）：ask=询问 last=恢复上次 explorer=接管已打开
        public string StartupOpen { get; set; } = "ask";

        // 接管系统文件夹打开（HKCU 注册表方式，关闭时自动还原系统默认）
        public bool TakeOverExplorer { get; set; }

        // 关闭窗口：true=最小化到托盘 false=直接退出
        public bool MinimizeOnClose { get; set; } = true;

        // 上次自动把桌面加入常用文件夹时的程序版本；版本变化后首个运行再补种一次
        public string SeededFavoriteVersion { get; set; }

        // 文件排序：name / date / type / size，是否递增，以及文件夹是否固定排最前
        public string SortBy { get; set; } = "name";
        public bool SortAsc { get; set; } = true;
        public bool SortFoldersFirst { get; set; } = true;

        // 是否显示左侧文件夹栏
        public bool ShowTree { get; set; } = true;

        // 批量重命名的序号前后缀：格式 = 前缀 + 序号 + 后缀，默认 "(" 和 ")"，均可为空
        public string BatchRenameNumPrefix { get; set; } = "(";
        public string BatchRenameNumSuffix { get; set; } = ")";

        public List<string> Favorites { get; set; } = new List<string>();
        public List<string> OpenFolders { get; set; } = new List<string>();

        // 并排布局每块面板的宽度权重
        public List<double> PanelWeights { get; set; } = new List<double>();

        // 网格布局的列权重与左右两列各自的行权重
        public List<double> GridColumnWeights { get; set; } = new List<double>();
        public List<double> GridRowWeights { get; set; } = new List<double>();
        public List<double> GridLeftRowWeights { get; set; } = new List<double>();
        public List<double> GridRightRowWeights { get; set; } = new List<double>();

        // 左侧「常用文件夹」区域高度
        public double FavoritesHeight { get; set; } = 150;

        // 设为顶层时被隐藏的根；null 表示完整树
        public string RootOverride { get; set; }

        // 上次关闭时展开的文件夹路径，下次启动原样恢复
        public List<string> ExpandedPaths { get; set; } = new List<string>();

        // 左侧文件夹栏宽度
        public double TreeWidth { get; set; } = 232;
    }
}
