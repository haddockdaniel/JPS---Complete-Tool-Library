using System;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jps.Core.Reports;
using Microsoft.Win32;

namespace Jps.Tools.Common
{
    /// <summary>
    /// The one report viewer every tool uses: grid, Open in Excel, Save as CSV.
    /// <see cref="Show"/> for an information report; <see cref="Confirm"/> for a pre-report the user approves before a change runs.
    /// </summary>
    public partial class ReportWindow
    {
        private readonly DataTable _table;
        private readonly string _title;

        private ReportWindow(string title, string subtitle, DataTable table)
        {
            InitializeComponent();
            Root.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            _table = table;
            _title = title;
            Title = title;
            TitleBar.Title = title;
            HeadingText.Text = title;
            SubtitleText.Text = subtitle ?? "";
            SubtitleText.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
            Grid.ItemsSource = table.DefaultView;
            EmptyText.Visibility = table.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = table.Rows.Count == 1 ? "1 row" : table.Rows.Count.ToString("N0") + " rows";
            ExcelButton.IsEnabled = table.Rows.Count > 0 || table.Columns.Count > 0;
        }

        /// <summary>Shows a report without blocking the tool.</summary>
        public static void Show(DependencyObject owner, string title, DataTable table, string subtitle = null)
        {
            var window = new ReportWindow(title, subtitle, table) { Owner = OwnerOf(owner) };
            window.Show();
        }

        /// <summary>Shows a pre-report and returns true only if the user clicks the continue button.</summary>
        public static bool Confirm(DependencyObject owner, string title, DataTable table, string subtitle, string continueText)
        {
            var window = new ReportWindow(title, subtitle, table) { Owner = OwnerOf(owner) };
            window.CancelButton.Visibility = Visibility.Visible;
            window.CloseButton.Content = continueText;
            window.CloseButton.IsEnabled = table.Rows.Count > 0;
            return window.ShowDialog() == true;
        }

        private static Window OwnerOf(DependencyObject owner) => owner == null ? Application.Current.MainWindow : Window.GetWindow(owner) ?? Application.Current.MainWindow;

        private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            // Column names with dots/slashes break binding paths; bind by indexer instead.
            if (e.Column is DataGridBoundColumn bound)
                bound.Binding = new System.Windows.Data.Binding("[" + e.PropertyName + "]")
                {
                    StringFormat = e.PropertyType == typeof(DateTime) ? "MM/dd/yyyy" : e.PropertyType == typeof(decimal) ? "#,##0.00" : null,
                };
            e.Column.Header = e.PropertyName;
        }

        private void OnOpenInExcel(object sender, RoutedEventArgs e)
        {
            NoticeText.Text = "";
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                ExcelExport.Open(_table, _title);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException)
            {
                NoticeText.Text = ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void OnSaveCsv(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                FileName = string.Join("_", _title.Split(Path.GetInvalidFileNameChars())) + ".csv",
                Filter = "CSV file (*.csv)|*.csv",
            };
            if (dialog.ShowDialog(this) != true)
                return;
            try
            {
                ExcelExport.SaveCsv(_table, dialog.FileName);
                NoticeText.Text = "";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                NoticeText.Text = "Couldn't save: " + ex.Message;
            }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            if (CancelButton.Visibility == Visibility.Visible)
                DialogResult = true;
            else
                Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
