using System.Windows;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 字号阶梯：档位 0 保持原始字号，每升一档所有字号 +1。
    /// </summary>
    public static class UiScaleService
    {
        public const int MaxStep = 7;

        private static readonly string[] Keys =
            { "FontXXs", "FontXs", "FontSm", "FontMd", "FontLg", "FontXl", "FontXxl" };

        private static readonly double[] Base =
            { 8, 9, 10, 11, 12, 13, 14 };

        public static int Step { get; private set; }

        public static void Apply(int step)
        {
            if (step < 0) step = 0;
            if (step > MaxStep) step = MaxStep;
            Step = step;

            var app = Application.Current;
            if (app == null) return;

            for (int i = 0; i < Keys.Length; i++)
                app.Resources[Keys[i]] = Base[i] + step;
        }
    }
}
