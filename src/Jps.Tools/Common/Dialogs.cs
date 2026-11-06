using System.Threading.Tasks;
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
            return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }

        public static async Task InformAsync(string title, string message)
        {
            var box = new Wpf.Ui.Controls.MessageBox { Title = title, Content = message, CloseButtonText = "OK", MaxWidth = 560 };
            await box.ShowDialogAsync();
        }

        /// <summary>Picks an import file. Returns null if cancelled.</summary>
        public static string PickImportFile(string filter = null)
        {
            var dialog = new OpenFileDialog { Filter = filter ?? Jps.Core.Import.SpreadsheetReader.FileFilter };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
