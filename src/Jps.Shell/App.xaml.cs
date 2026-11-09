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
            var profile = Arg(e.Args, "--profile");
            if (profile != null)
                // Support: "--profile out.txt" opens every tool twice off screen and records how long the window froze.
                Dispatcher.BeginInvoke(new Action(() => Profile(profile)), DispatcherPriority.Loaded);
            else if (snapshot != null)
                Dispatcher.BeginInvoke(new Action(() => TakeSnapshot(snapshot, Arg(e.Args, "--page"), Arg(e.Args, "--theme"), Arg(e.Args, "--size"))), DispatcherPriority.Loaded);
            else if (Arg(e.Args, "--page") != null)
                // Support/testing: open straight on a tool ("--page JPS.MATCLOSER"), optionally with "--company Juris9999002".
                Dispatcher.BeginInvoke(new Action(() => ((MainViewModel)MainWindow.DataContext).SelectNav(Arg(e.Args, "--page"))), DispatcherPriority.Loaded);
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

        private async void Profile(string path)
        {
            var window = (MainWindow)MainWindow;
            window.ShowActivated = false;
            window.Left = -20000;
            var vm = (MainViewModel)window.DataContext;
            var log = new System.Text.StringBuilder("Tool\tOpen\tSync ms\tTo idle ms\tLongest freeze ms\tReady (lists loaded) ms\tNote\n");

            // Heartbeat: a 25 ms timer; any longer gap between ticks means the UI thread was blocked that long.
            var last = System.Diagnostics.Stopwatch.StartNew();
            double maxGap = 0;
            var beat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
            beat.Tick += (_, __) => { maxGap = Math.Max(maxGap, last.Elapsed.TotalMilliseconds); last.Restart(); };
            beat.Start();

            var ids = new System.Collections.Generic.List<string>();
            foreach (var t in Jps.Core.Catalog.ToolCatalog.All) ids.Add(t.Id);
            foreach (var pass in new[] { "first", "second" })
                foreach (var id in ids)
                {
                    vm.SelectNav("home");
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    await System.Threading.Tasks.Task.Delay(300);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    vm.SelectNav(id);
                    var sync = sw.Elapsed.TotalMilliseconds;
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var idle = sw.Elapsed.TotalMilliseconds;
                    last.Restart(); maxGap = 0;
                    // Time until the tool's "Loading…" overlay goes away (lists read from the database).
                    while (sw.Elapsed.TotalSeconds < 120)
                    {
                        await System.Threading.Tasks.Task.Delay(50);
                        if (sw.Elapsed.TotalMilliseconds > idle + 300 && !AnyBusy(window)) break;
                    }
                    var loaded = sw.Elapsed.TotalMilliseconds;
                    var busy = AnyBusyText(window);
                    await System.Threading.Tasks.Task.Delay(2000);
                    log.AppendFormat("{0}\t{1}\t{2:N0}\t{3:N0}\t{4:N0}\t{5:N0}\t{6}\n", id, pass, sync, idle, maxGap, loaded, busy);
                    File.WriteAllText(path, log.ToString());
                }
            beat.Stop();
            Shutdown();
        }

        private static System.Collections.Generic.IEnumerable<Jps.Tools.Common.ToolFrame> Frames(DependencyObject root)
        {
            if (root is Jps.Tools.Common.ToolFrame f) yield return f;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var x in Frames(VisualTreeHelper.GetChild(root, i))) yield return x;
        }

        private static bool AnyBusy(Window w) { foreach (var f in Frames(w)) if (f.IsBusy) return true; return false; }

        private static string AnyBusyText(Window w) { foreach (var f in Frames(w)) if (f.IsBusy) return "STILL BUSY: " + f.BusyText; return ""; }

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
