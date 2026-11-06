using System.Windows;
using System.Windows.Controls;
using Jps.Shell.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Jps.Shell
{
    public partial class MainWindow : FluentWindow
    {
        private bool _ready;

        public MainWindow()
        {
            InitializeComponent();
            Theme.Apply(ApplicationTheme.Dark);
            DarkMode.IsChecked = true;
            _ready = true;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Collapse the rail to icons on narrow windows so tool pages keep their room.
            ((MainViewModel)DataContext).IsNavCompact = ActualWidth < 1000;
        }

        private void OnThemeChosen(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Theme.Apply((string)((RadioButton)sender).Tag == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark);
        }

        /// <summary>Used by snapshot mode so the switch shows the theme that was rendered.</summary>
        public void ShowTheme(ApplicationTheme theme)
        {
            _ready = false;
            (theme == ApplicationTheme.Light ? LightMode : DarkMode).IsChecked = true;
            _ready = true;
        }
    }
}
