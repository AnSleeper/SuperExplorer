using System;
using System.Windows;

namespace SuperExplorer.Services
{
    public static class ThemeService
    {
        private static readonly string[] Names = { "Pink", "Violet", "Cyan", "Green", "Amber", "Mono" };

        public static string Current { get; private set; } = "Mono";

        public static void Apply(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Mono";
            if (Array.IndexOf(Names, name) < 0) name = "Mono";

            var app = Application.Current;
            if (app == null) return;

            var dict = new ResourceDictionary
            {
                Source = new Uri("Themes/Theme." + name + ".xaml", UriKind.Relative)
            };

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 1) merged[1] = dict;
            else merged.Add(dict);

            Current = name;
        }

        public static string[] All { get { return Names; } }
    }
}
