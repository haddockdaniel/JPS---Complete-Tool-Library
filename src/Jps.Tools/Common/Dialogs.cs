using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Jps.Tools.Common
{
    public static class Dialogs
    {
        /// <summary>Yes/no question before a change. Returns true only for the primary button.</summary>
        public static async Task<bool> ConfirmAsync(string title, string message, string primaryText)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primaryText,
                CloseButtonText = "Cancel",
                MaxWidth = 560,
            };
            CenterOnApp(box);
            return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }

        public static async Task InformAsync(string title, string message)
        {
            var box = new Wpf.Ui.Controls.MessageBox { Title = title, Content = message, CloseButtonText = "OK", MaxWidth = 560 };
            CenterOnApp(box);
            await box.ShowDialogAsync();
        }

        /// <summary>Picks an import file. Returns null if cancelled.</summary>
        public static string PickImportFile(string filter = null)
        {
            var dialog = new OpenFileDialog { Filter = filter ?? Jps.Core.Import.SpreadsheetReader.FileFilter };
            var owner = ActiveAppWindow();
            return (owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
        }

        /// <summary>
        /// Opens the dialog over the app's window, so it appears on the same monitor (without an owner, Windows put it on
        /// the primary screen while the app was on another one, where it looked like the app had frozen).
        /// </summary>
        private static void CenterOnApp(Window dialog)
        {
            var owner = ActiveAppWindow();
            if (owner == null) return;
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        private static Window ActiveAppWindow()
        {
            var app = Application.Current;
            if (app == null) return null;
            return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                   ?? (app.MainWindow != null && app.MainWindow.IsVisible ? app.MainWindow : null);
        }
    }
}
