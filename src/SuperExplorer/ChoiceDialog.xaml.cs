using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SuperExplorer
{
    /// <summary>
    /// 多选项询问框：按传入顺序生成按钮，返回用户选择的序号（第一个选项为安全默认）。
    /// </summary>
    public partial class ChoiceDialog : Window
    {
        public int Selected { get; private set; } = -1;

        public ChoiceDialog(string message, string[] options, string detail = null)
        {
            InitializeComponent();
            MessageText.Text = message;

            if (!string.IsNullOrEmpty(detail))
            {
                DetailText.Text = detail;
                DetailText.Visibility = Visibility.Visible;
            }

            Loaded += (s, e) => Activate();

            for (int i = 0; i < options.Length; i++)
            {
                var idx = i;
                var btn = new Button
                {
                    Content = options[i],
                    Style = (Style)Application.Current.Resources["FlatButton"],
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                btn.Click += (s2, e2) =>
                {
                    Selected = idx;
                    DialogResult = true;
                };
                Buttons.Children.Add(btn);
            }
        }

        private void Root_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
