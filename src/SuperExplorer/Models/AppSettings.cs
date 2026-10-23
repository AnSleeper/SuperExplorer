using System.Collections.Generic;

namespace SuperExplorer.Models
{
    public class AppSettings
    {
        public string Theme { get; set; } = "Pink";
        public string Layout { get; set; } = "row";
        public double TileSize { get; set; } = 140;
        public int FontStep { get; set; }
        public bool ConfirmDelete { get; set; } = true;

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
