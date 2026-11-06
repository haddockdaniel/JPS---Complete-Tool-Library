using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Jps.Shell.ViewModels;
using Wpf.Ui.Appearance;

namespace Jps.Shell
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnUnhandledException;
            base.OnStartup(e);

            // Support/docs: "JPS Tool Library.exe --snapshot out.png [--page JPS.MATCLOSER] [--theme dark] [--size 1320x840]"
            // renders the window to a PNG off screen and exits.
            var snapshot = Arg(e.Args, "--snapshot");
            if (snapshot != null)
                Dispatcher.BeginInvoke(new Action(() => TakeSnapshot(snapshot, Arg(e.Args, "--page"), Arg(e.Args, "--theme"), Arg(e.Args, "--size"))), DispatcherPriority.Loaded);
        }

        private void TakeSnapshot(string path, string page, string theme, string size)
        {
            var window = (MainWindow)MainWindow;
            window.ShowActivated = false;
            window.Left = -20000;
            if (size != null)
            {
                var parts = size.Split('x');
                window.Width = double.Parse(parts[0]);
                window.Height = double.Parse(parts[1]);
            }
            if (theme != null)
            {
                var chosen = theme == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
                Theme.Apply(chosen);
                window.ShowTheme(chosen);
            }
            if (page != null)
                ((MainViewModel)window.DataContext).SelectNav(page);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(path))
                    encoder.Save(file);
                Shutdown();
            };
            timer.Start();
        }

        private static string Arg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // A tool failing must not take the whole library down with it.
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "JPS Tool Library.log"), DateTime.Now + "  " + e.Exception + Environment.NewLine);
            MessageBox.Show(e.Exception.Message, "JPS Tool Library", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
