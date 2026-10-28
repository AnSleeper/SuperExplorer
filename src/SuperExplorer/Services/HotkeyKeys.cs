using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace SuperExplorer.Services
{
    /// <summary>
    /// 全局热键的「文本 ⇄ 修饰键 + 虚拟键」换算。
    /// 设置里存的是可读文本（如 Alt+W），注册时才换算成 RegisterHotKey 要的参数，
    /// 这样配置文件手改也能看懂。
    /// </summary>
    public static class HotkeyKeys
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public const string DefaultSpec = "Alt+W";

        private static readonly Dictionary<string, Key> NameMap = BuildNameMap();

        private static Dictionary<string, Key> BuildNameMap()
        {
            var map = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);

            for (Key k = Key.A; k <= Key.Z; k++) map[k.ToString()] = k;
            for (Key k = Key.D0; k <= Key.D9; k++) map[((int)(k - Key.D0)).ToString()] = k;
            for (Key k = Key.NumPad0; k <= Key.NumPad9; k++) map["Num" + (int)(k - Key.NumPad0)] = k;
            for (int i = 1; i <= 24; i++) map["F" + i] = Key.F1 + (i - 1);

            map["`"] = Key.Oem3;
            map["-"] = Key.OemMinus;
            map["="] = Key.OemPlus;
            map["["] = Key.OemOpenBrackets;
            map["]"] = Key.OemCloseBrackets;
            map[";"] = Key.OemSemicolon;
            map["'"] = Key.OemQuotes;
            map[","] = Key.OemComma;
            map["."] = Key.OemPeriod;
            map["/"] = Key.OemQuestion;
            map["\\"] = Key.OemPipe;
            map["Space"] = Key.Space;

            return map;
        }

        /// <summary>按键 → 显示名。不认识的键直接返回 Key 的字符串形式（如 Return）。</summary>
        public static string KeyName(Key k)
        {
            if (k >= Key.A && k <= Key.Z) return k.ToString();
            if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num" + (int)(k - Key.NumPad0);
            if (k >= Key.F1 && k <= Key.F24) return "F" + ((int)(k - Key.F1) + 1);

            switch (k)
            {
                case Key.Oem3: return "`";
                case Key.OemMinus: return "-";
                case Key.OemPlus: return "=";
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
                case Key.OemSemicolon: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                case Key.OemPipe: return "\\";
                case Key.Space: return "Space";
                default: return k.ToString();
            }
        }

        /// <summary>组装可读文本，修饰键固定按 Ctrl→Alt→Shift→Win 排序。</summary>
        public static string Format(uint mods, Key key)
        {
            var s = "";
            if ((mods & MOD_CONTROL) != 0) s += "Ctrl+";
            if ((mods & MOD_ALT) != 0) s += "Alt+";
            if ((mods & MOD_SHIFT) != 0) s += "Shift+";
            if ((mods & MOD_WIN) != 0) s += "Win+";
            return s + KeyName(key);
        }

        /// <summary>把 WPF 的 ModifierKeys 换成 Win32 的 MOD_* 位标志。</summary>
        public static uint FromModifiers(ModifierKeys m)
        {
            uint mods = 0;
            if ((m & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
            if ((m & ModifierKeys.Alt) != 0) mods |= MOD_ALT;
            if ((m & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
            if ((m & ModifierKeys.Windows) != 0) mods |= MOD_WIN;
            return mods;
        }

        /// <summary>
        /// 解析文本形式的组合键。必须同时有修饰键和主键，
        /// 否则注册出来的热键会把普通打字吃掉。
        /// </summary>
        public static bool TryParse(string spec, out uint mods, out uint vk, out string text)
        {
            mods = 0;
            vk = 0;
            text = null;

            if (string.IsNullOrWhiteSpace(spec)) return false;

            var parts = spec.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            Key key = Key.None;

            foreach (var raw in parts)
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;

                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Control", StringComparison.OrdinalIgnoreCase)) { mods |= MOD_CONTROL; continue; }
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { mods |= MOD_ALT; continue; }
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { mods |= MOD_SHIFT; continue; }
                if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Windows", StringComparison.OrdinalIgnoreCase)) { mods |= MOD_WIN; continue; }

                Key parsed;
                if (!NameMap.TryGetValue(p, out parsed)) return false;
                if (key != Key.None) return false;   // 出现两个主键，非法
                key = parsed;
            }

            if (key == Key.None || mods == 0) return false;

            int v = KeyInterop.VirtualKeyFromKey(key);
            if (v == 0) return false;

            vk = (uint)v;
            text = Format(mods, key);
            return true;
        }
    }
}
