using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.BillCopySettings
{
    public sealed class BillCopySettingsTool : IJurisTool
    {
        public string Id => "JPS.BILLCOPY";
        public FrameworkElement CreateView(IToolContext context) => new BillCopySettingsView(context);
    }

    /// <summary>
    /// Sets the Bill Copy settings (number of copies, print bill, print A/R statement, email and export format, comment) on every
    /// BillCopy row of the bill-tos of matters picked by billing timekeeper, originating timekeeper or client, or row by row from
    /// an imported .csv/.xlsx template. Ported from "JPS - Bill Copy Settings" with these fixes: "* All Employees" broke after a
    /// company switch; a non-numeric or too-large number of copies crashed the tool; the pre-report showed only the last 1000
    /// matters; the comment was concatenated into SQL (a quote broke it) and template codes likewise; updates ran without a
    /// transaction; the import wrote immediately with no pre-report or confirmation and applied valid rows around bad ones;
    /// template rows were dropped after 21 blank rows anywhere, error line numbers were off by one, only the last error per row
    /// was shown, untrimmed or short CSV lines failed or crashed, invalid copies/long comments aborted the import half-way, and
    /// rows matching no bill-to were silently skipped; nothing was written to the UtilityLog.
    /// </summary>
    public partial class BillCopySettingsView : UserControl
    {
        private static readonly LookupItem AllTimekeepers = new LookupItem(null, "", "* All timekeepers");

        private readonly IToolContext _context;
        private readonly BillCopyService _service;
        private readonly bool _ready;

        private string _templatePath;
        private TemplateCheck _templateCheck;

        public BillCopySettingsView(IToolContext context)
        {
            _context = context;
            _service = new BillCopyService(context.Session);
            InitializeComponent();
            EmailBox.ItemsSource = BillCopyValues.FormatNames.Take(BillCopyValues.MaxEmailFormat + 1).ToList();
            ExportBox.ItemsSource = BillCopyValues.FormatNames.ToList();
            EmailBox.SelectedIndex = 0;
            ExportBox.SelectedIndex = 0;
            _ready = true;
            Loaded += async (_, __) =>
            {
                if (TkprBox.ItemsSource != null) return; // already loaded (page re-shown)
                var lists = await Frame.RunAsync("Loading timekeepers and clients…", () =>
                    Tuple.Create(_service.LoadTimekeepers(), _service.LoadClients()));
                if (lists == null) return;
                TkprBox.ItemsSource = new[] { AllTimekeepers }.Concat(lists.Item1).ToList();
                ClientList.ItemsSource = lists.Item2;
                if (lists.Item2.Count == 0)
                    Frame.ShowWarning("This company has no clients, so there's nothing to change.");
                UpdateState();
            };
        }

        private BillCopyMode Mode =>
            ModeOrig.IsChecked == true ? BillCopyMode.OriginatingTimekeeper
            : ModeClient.IsChecked == true ? BillCopyMode.Client
            : ModeTemplate.IsChecked == true ? BillCopyMode.Template
            : BillCopyMode.BillingTimekeeper;

        // ---------- input state ----------

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var mode = Mode;
            TkprPanel.Visibility = mode == BillCopyMode.BillingTimekeeper || mode == BillCopyMode.OriginatingTimekeeper ? Visibility.Visible : Visibility.Collapsed;
            ClientPanel.Visibility = mode == BillCopyMode.Client ? Visibility.Visible : Visibility.Collapsed;
            TemplatePanel.Visibility = mode == BillCopyMode.Template ? Visibility.Visible : Visibility.Collapsed;
            SettingsCard.Visibility = mode == BillCopyMode.Template ? Visibility.Collapsed : Visibility.Visible;
            SelectTitle.Text = mode == BillCopyMode.BillingTimekeeper ? "2  Pick the billing timekeeper"
                : mode == BillCopyMode.OriginatingTimekeeper ? "2  Pick the originating timekeeper"
                : mode == BillCopyMode.Client ? "2  Check the clients"
                : "2  Choose the template file";
            TkprHint.Text = mode == BillCopyMode.OriginatingTimekeeper
                ? "Open matters that have this originating timekeeper are changed."
                : "Open matters whose bill-to has this billing timekeeper are changed.";
            PreviewButton.Content = mode == BillCopyMode.Template ? "Show rows" : "Show matters";
            Frame.ClearMessage();
            UpdateState();
        }

        private void OnInputChanged(object sender, EventArgs e)
        {
            if (!_ready) return;
            UpdateState();
        }

        // Clicking a setting's input ticks its box, as in the original.
        private void OnCopiesFocus(object sender, RoutedEventArgs e) => CopiesCheck.IsChecked = true;
        private void OnPrintFocus(object sender, RoutedEventArgs e) => PrintCheck.IsChecked = true;
        private void OnARFocus(object sender, RoutedEventArgs e) => ARCheck.IsChecked = true;
        private void OnEmailFocus(object sender, RoutedEventArgs e) => EmailCheck.IsChecked = true;
        private void OnExportFocus(object sender, RoutedEventArgs e) => ExportCheck.IsChecked = true;
        private void OnCommentFocus(object sender, RoutedEventArgs e) => CommentCheck.IsChecked = true;

        /// <summary>Reads the ticked settings. Returns null and sets <paramref name="problem"/> when they can't be used.</summary>
        private BillCopyValues ReadValues(out string problem)
        {
            problem = null;
            var values = new BillCopyValues();
            if (CopiesCheck.IsChecked == true)
            {
                if (!short.TryParse(CopiesBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var copies))
                {
                    problem = "The number of copies must be a whole number from 0 to 32767.";
                    return null;
                }
                values.Copies = copies;
            }
            if (PrintCheck.IsChecked == true) values.PrintBill = PrintBox.SelectedIndex;
            if (ARCheck.IsChecked == true) values.PrintAR = ARBox.SelectedIndex;
            if (EmailCheck.IsChecked == true) values.Email = EmailBox.SelectedIndex;
            if (ExportCheck.IsChecked == true) values.Export = ExportBox.SelectedIndex;
            if (CommentCheck.IsChecked == true) values.Comment = CommentBox.Text.Trim();
            if (values.IsEmpty)
            {
                problem = "Tick at least one setting to change.";
                return null;
            }
            return values;
        }

        /// <summary>Reads the matter selection. Returns null and sets <paramref name="problem"/> when nothing is picked.</summary>
        private MatterSelection ReadSelection(out string problem)
        {
            problem = null;
            var mode = Mode;
            if (mode == BillCopyMode.BillingTimekeeper || mode == BillCopyMode.OriginatingTimekeeper)
            {
                var item = TkprBox.SelectedItem as LookupItem;
                var kind = mode == BillCopyMode.BillingTimekeeper ? "billing" : "originating";
                if (item == null)
                {
                    problem = "Pick a" + (kind == "billing" ? " " : "n ") + kind + " timekeeper.";
                    return null;
                }
                return new MatterSelection
                {
                    Mode = mode,
                    TimekeeperId = item == AllTimekeepers ? (int?)null : Convert.ToInt32(item.Key),
                    Description = "by " + kind + " timekeeper " + (item == AllTimekeepers ? "(all)" : item.Display),
                };
            }
            if (mode == BillCopyMode.Client)
            {
                var all = ClientList.ItemsSource?.ToList() ?? new List<LookupItem>();
                var picked = ClientList.Checked;
                if (picked.Count == 0)
                {
                    problem = "Check at least one client.";
                    return null;
                }
                var everyone = picked.Count == all.Count;
                return new MatterSelection
                {
                    Mode = mode,
                    ClientIds = everyone ? null : picked.Select(i => Convert.ToInt32(i.Key)).ToList(),
                    Description = everyone ? "all clients" : picked.Count == 1 ? "client " + picked[0].Code : picked.Count + " clients",
                };
            }
            problem = "Template rows are not a matter selection.";
            return null;
        }

        private void UpdateState()
        {
            string problem;
            if (Mode == BillCopyMode.Template)
            {
                problem = _templatePath == null ? "Choose a template file."
                    : _templateCheck == null ? "The file couldn't be read. Fix it and choose it again."
                    : _templateCheck.ErrorCount > 0 ? "Fix the rows with errors and choose the file again."
                    : _templateCheck.Rows.Count == 0 ? "The file has no rows to import."
                    : null;
                PreviewButton.IsEnabled = problem == null;
            }
            else
            {
                var selection = ReadSelection(out problem);
                PreviewButton.IsEnabled = selection != null;
                if (selection != null) ReadValues(out problem);
            }
            ApplyButton.IsEnabled = problem == null;
            ReadyHint.Text = problem ?? "";
        }

        // ---------- template ----------

        private async void OnChooseFile(object sender, RoutedEventArgs e)
        {
            var path = Dialogs.PickImportFile("Data Files (*.csv;*.xlsx)|*.csv;*.xlsx");
            if (path == null) return;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            _templatePath = path;
            _templateCheck = null;
            ErrorsButton.Visibility = Visibility.Collapsed;
            FileText.Text = Path.GetFileName(path);
            if (ext != ".csv" && ext != ".xlsx")
            {
                _templatePath = null;
                Frame.ShowWarning("This kind of file can't be read. Choose an Excel workbook (.xlsx) or a .csv file.");
                UpdateState();
                return;
            }
            await CheckTemplateAsync(path, true);
            UpdateState();
        }

        /// <summary>Reads and validates the file; shows the errors. Returns the check, or null if the file couldn't be read.</summary>
        private async System.Threading.Tasks.Task<TemplateCheck> CheckTemplateAsync(string path, bool announceSuccess)
        {
            var check = await Frame.RunAsync("Checking " + Path.GetFileName(path) + "…", () => _service.CheckTemplate(path));
            _templateCheck = check;
            if (check == null) return null;
            ErrorsButton.Visibility = check.ErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            FileText.Text = Path.GetFileName(path) + ": " + check.Rows.Count + " valid row(s), " + check.ErrorCount + " row(s) with errors.";
            if (check.ErrorCount > 0)
            {
                Frame.ShowWarning(check.ErrorCount + " row(s) in the file have errors, so nothing can be imported yet. Fix them and choose the file again.");
                ShowErrors(check);
            }
            else if (check.Rows.Count == 0)
                Frame.ShowWarning("The file has no data rows below the header.");
            else if (announceSuccess)
                Frame.ShowInfo("Every row checked out. " + check.Rows.Count + " row(s) are ready to import.");
            return check;
        }

        private void OnShowErrors(object sender, RoutedEventArgs e)
        {
            if (_templateCheck != null) ShowErrors(_templateCheck);
        }

        private void ShowErrors(TemplateCheck check) =>
            ReportWindow.Show(this, "Rows with errors", check.Errors, Path.GetFileName(_templatePath));

        // ---------- reports and the change ----------

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            if (Mode == BillCopyMode.Template)
            {
                if (_templateCheck == null) return;
                ReportWindow.Show(this, "Rows to be changed", BillCopyService.TemplatePreReport(_templateCheck.Rows), Path.GetFileName(_templatePath));
                return;
            }
            var selection = ReadSelection(out var problem);
            if (selection == null)
            {
                Frame.ShowWarning(problem);
                return;
            }
            var table = await Frame.RunAsync("Finding matters…", () => _service.MattersToChange(selection));
            if (table == null) return;
            ReportWindow.Show(this, "Matters to be changed", table, "Matters " + selection.Description);
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (Mode == BillCopyMode.Template)
                await ApplyTemplateAsync();
            else
                await ApplySelectionAsync();
        }

        private async System.Threading.Tasks.Task ApplySelectionAsync()
        {
            var selection = ReadSelection(out var problem);
            var values = selection == null ? null : ReadValues(out problem);
            if (values == null)
            {
                Frame.ShowWarning(problem);
                return;
            }

            var table = await Frame.RunAsync("Finding matters…", () => _service.MattersToChange(selection));
            if (table == null) return;
            if (table.Rows.Count == 0)
            {
                Frame.ShowWarning("No open matters match this selection, so there's nothing to change.");
                return;
            }
            var matters = table.Rows.Count;
            var shared = table.Rows.Cast<System.Data.DataRow>().Count(r => Convert.ToInt32(r["Other Matters Sharing This Bill-To"]) > 0);
            var subtitle = "Matters " + selection.Description + ". Setting " + values.Describe() + "."
                           + (shared > 0 ? " " + shared + " of these matters share their bill-to with other matters, which change too." : "")
                           + " This can't be undone from here.";
            if (!ReportWindow.Confirm(this, "Matters to be changed", table, subtitle, "Update " + matters + " matter(s)"))
                return;

            var changed = await Frame.RunAsync("Updating bill copy settings…", () => (int?)_service.ApplySelection(selection, values, matters));
            if (changed == null || Frame.HasMessage) return; // error already shown
            if (changed == 0)
                Frame.ShowWarning("These matters' bill-tos have no bill copies set up, so nothing was changed.");
            else
                Frame.ShowSuccess("Updated " + changed + " bill copy setting(s) for " + matters + " matter(s): " + values.Describe() + ".");
        }

        private async System.Threading.Tasks.Task ApplyTemplateAsync()
        {
            var path = _templatePath;
            if (path == null) return;

            // Check the file again: it (or Juris) may have changed since it was chosen.
            var check = await CheckTemplateAsync(path, false);
            UpdateState();
            if (check == null || check.ErrorCount > 0) return;
            if (check.Rows.Count == 0)
            {
                Frame.ShowWarning("The file has no rows to import.");
                return;
            }

            var rows = check.Rows;
            if (!ReportWindow.Confirm(this, "Rows to be changed", BillCopyService.TemplatePreReport(rows),
                    "This will update the bill copy settings for " + rows.Count + " template row(s), in file order, all together. This can't be undone from here.",
                    "Update " + rows.Count + " row(s)"))
                return;

            var fileName = Path.GetFileName(path);
            var changed = await Frame.RunAsync("Importing bill copy settings…", () => (int?)_service.ApplyTemplate(rows, fileName));
            if (changed == null || Frame.HasMessage) return; // error already shown, nothing saved
            Frame.ShowSuccess("Imported " + rows.Count + " row(s) with no errors: " + changed + " bill copy setting(s) updated.");
        }
    }
}
