using System.Windows;
using System.Windows.Input;

namespace SuperExplorer
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string message, string path)
        {
            InitializeComponent();
            MessageText.Text = message;
            PathText.Text = path;
            Loaded += (s, e) => Activate();
        }

        private void Root_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
