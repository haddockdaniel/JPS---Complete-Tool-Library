using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Jps.Tools.Common
{
    /// <summary>
    /// Wraps every tool's view: a status message bar on top, the tool's content, and a busy overlay while work runs.
    /// <see cref="RunAsync"/> is the one way tools run database work: off the UI thread, errors shown, never a crash.
    /// </summary>
    public class ToolFrame : ContentControl
    {
        static ToolFrame()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ToolFrame), new FrameworkPropertyMetadata(typeof(ToolFrame)));
        }

        public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(ToolFrame));
        public static readonly DependencyProperty BusyTextProperty = DependencyProperty.Register(nameof(BusyText), typeof(string), typeof(ToolFrame));
        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(ToolFrame));
        public static readonly DependencyProperty MessageTitleProperty = DependencyProperty.Register(nameof(MessageTitle), typeof(string), typeof(ToolFrame));
        public static readonly DependencyProperty HasMessageProperty = DependencyProperty.Register(nameof(HasMessage), typeof(bool), typeof(ToolFrame));
        public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(nameof(Severity), typeof(InfoBarSeverity), typeof(ToolFrame));

        public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
        public string BusyText { get => (string)GetValue(BusyTextProperty); set => SetValue(BusyTextProperty, value); }
        public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
        public string MessageTitle { get => (string)GetValue(MessageTitleProperty); set => SetValue(MessageTitleProperty, value); }
        public bool HasMessage { get => (bool)GetValue(HasMessageProperty); set => SetValue(HasMessageProperty, value); }
        public InfoBarSeverity Severity { get => (InfoBarSeverity)GetValue(SeverityProperty); set => SetValue(SeverityProperty, value); }

        public void ShowSuccess(string message) => Show("Done", message, InfoBarSeverity.Success);
        public void ShowWarning(string message) => Show("Check this", message, InfoBarSeverity.Warning);
        public void ShowError(string message) => Show("Something went wrong", message, InfoBarSeverity.Error);
        public void ShowInfo(string message) => Show(null, message, InfoBarSeverity.Informational);
        public void ClearMessage() => HasMessage = false;

        private void Show(string title, string message, InfoBarSeverity severity)
        {
            MessageTitle = title;
            Message = message;
            Severity = severity;
            HasMessage = true;
        }

        /// <summary>Runs <paramref name="work"/> on a background thread with the busy overlay. Returns false (and shows the error) if it threw.</summary>
        public async Task<bool> RunAsync(string busyText, Action work)
        {
            var result = await RunAsync(busyText, () => { work(); return true; });
            return result;
        }

        public async Task<T> RunAsync<T>(string busyText, Func<T> work)
        {
            ClearMessage();
            BusyText = busyText;
            IsBusy = true;
            try
            {
                return await Task.Run(work);
            }
            catch (Exception ex)
            {
                ShowError(Describe(ex));
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JPS Tool Library.log"),
                    DateTime.Now + "  " + ex + Environment.NewLine);
                return default(T);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is System.Data.SqlClient.SqlException sql)
                return "The database reported a problem, so this step was stopped and nothing was changed. " + sql.Message;
            return ex.Message;
        }
    }
}
