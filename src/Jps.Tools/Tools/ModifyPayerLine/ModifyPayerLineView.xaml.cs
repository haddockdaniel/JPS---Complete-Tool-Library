using System;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.ModifyPayerLine
{
    public sealed class ModifyPayerLineTool : IJurisTool
    {
        public string Id => "JPS.CRPAYER";
        public FrameworkElement CreateView(IToolContext context) => new ModifyPayerLineView(context);
    }

    /// <summary>
    /// Changes the payer text of a posted (or unposted) cash receipt without reversing it, for one receipt (batch + record) or
    /// every receipt in a batch. Ported from "JPS - Modify Payor Line in Cash Receipts" with these fixes: only
    /// CashReceipt.CRPayor was changed, leaving the old payer on every A/R allocation line (CRARAlloc.CRAPayor) — both now change
    /// in one transaction; the new payer was concatenated into SQL, so an apostrophe crashed the tool; running with no batch or
    /// record picked crashed after a confirmation that said "Batch: 0"; rebinding the record list could crash; a new date or
    /// batch left the previous batch's records and payer on screen, so Run could hit a record from the old batch; ticking
    /// "all records" didn't refresh the current payer; nothing was written to the UtilityLog.
    /// </summary>
    public partial class ModifyPayerLineView : UserControl
    {
        private readonly PayerService _service;
        private readonly bool _ready;
        private int _version; // discards results of lookups overtaken by a newer selection

        public ModifyPayerLineView(IToolContext context)
        {
            _service = new PayerService(context.Session);
            InitializeComponent();
            _ready = true;
            Loaded += async (_, __) =>
            {
                if (DateBox.SelectedDate != null) return; // already loaded (page re-shown)
                var max = await Frame.RunAsync("Loading…", () => (int?)_service.MaxPayerLength());
                if (max == null) return;
                NewBox.MaxLength = max.Value;
                NewHint.Text = "Up to " + max.Value + " characters. The payer changes on the receipt and on each of its A/R allocation lines. "
                               + "General ledger, trust ledger and check register entries keep their text.";
                DateBox.SelectedDate = DateTime.Today; // loads the batches
            };
        }

        private int? Batch => BatchBox.SelectedItem as int?;
        private int? Record => RecordBox.SelectedItem as int?;
        private bool AllRecords => AllBox.IsChecked == true;

        private async void OnDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            BatchBox.ItemsSource = null; // (clearing raises the batch/record handlers, so take the version afterwards)
            RecordBox.ItemsSource = null;
            CurrentBox.Text = "";
            UpdateState();
            var version = ++_version;
            var date = DateBox.SelectedDate;
            if (date == null) return;

            var batches = await Frame.RunAsync("Finding batches…", () => _service.Batches(date.Value));
            if (batches == null || version != _version) return;
            BatchBox.ItemsSource = batches;
            if (batches.Count == 0)
                Frame.ShowWarning("There are no cash receipt batches with receipts dated " + date.Value.ToString("MM/dd/yyyy") + ".");
            else if (batches.Count == 1)
                BatchBox.SelectedIndex = 0;
        }

        private async void OnBatchChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            RecordBox.ItemsSource = null;
            CurrentBox.Text = "";
            UpdateState();
            var version = ++_version;
            var batch = Batch;
            if (batch == null) return;

            var records = await Frame.RunAsync("Finding receipts…", () => _service.Records(batch.Value));
            if (records == null || version != _version) return;
            RecordBox.ItemsSource = records;
            if (records.Count == 0)
                Frame.ShowWarning("Batch " + batch + " has no receipts.");
            await RefreshCurrentAsync();
        }

        private async void OnRecordChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            await RefreshCurrentAsync();
        }

        private async void OnAllChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            RecordBox.IsEnabled = !AllRecords;
            await RefreshCurrentAsync();
        }

        private void OnNewChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready) return;
            UpdateState();
        }

        private async System.Threading.Tasks.Task RefreshCurrentAsync()
        {
            var version = ++_version;
            CurrentBox.Text = "";
            UpdateState();
            var batch = Batch;
            var record = Record;
            if (batch == null) return;
            if (AllRecords)
            {
                CurrentBox.Text = "(every receipt in the batch)";
                UpdateState();
                return;
            }
            if (record == null) return;

            var payer = await Frame.RunAsync("Reading the receipt…", () => _service.CurrentPayer(batch.Value, record.Value));
            if (payer == null || version != _version) return;
            CurrentBox.Text = payer;
            UpdateState();
        }

        private string Problem()
        {
            if (Batch == null) return "Pick a batch.";
            if (!AllRecords && Record == null) return "Pick a receipt record, or tick \"Change every receipt in this batch\".";
            if (NewBox.Text.Trim().Length == 0) return "Enter the new payer.";
            if (!AllRecords && string.Equals(NewBox.Text.Trim(), CurrentBox.Text, StringComparison.Ordinal))
                return "The new payer is the same as the current one.";
            return null;
        }

        private void UpdateState()
        {
            var problem = Problem();
            ApplyButton.IsEnabled = problem == null;
            PreviewButton.IsEnabled = Batch != null && (AllRecords || Record != null);
            ReadyHint.Text = problem ?? "";
        }

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var batch = Batch;
            if (batch == null) return;
            var record = AllRecords ? null : Record;
            if (!AllRecords && record == null) return;
            var newPayer = NewBox.Text.Trim();
            var table = await Frame.RunAsync("Finding receipts…", () => _service.Receipts(batch.Value, record, newPayer));
            if (table == null) return;
            ReportWindow.Show(this, "Receipts to be changed", table, "Batch " + batch + ", record " + (record == null ? "All" : record.ToString()));
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            var problem = Problem();
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return;
            }
            var batch = Batch.Value;
            var record = AllRecords ? null : Record;
            var newPayer = NewBox.Text.Trim();
            var oldPayer = record == null ? "(every receipt in the batch)" : CurrentBox.Text;
            var which = "batch " + batch + ", record " + (record == null ? "All" : record.ToString());

            var table = await Frame.RunAsync("Finding receipts…", () => _service.Receipts(batch, record, newPayer));
            if (table == null) return;
            if (table.Rows.Count == 0)
            {
                Frame.ShowWarning("No receipts were found for " + which + ". Nothing was changed.");
                return;
            }
            if (!ReportWindow.Confirm(this, "Receipts to be changed", table,
                    "This changes the payer to \"" + newPayer + "\" for " + which + ", on the receipt and its A/R allocation lines. This can't be undone from here.",
                    "Change payer on " + table.Rows.Count + " receipt(s)"))
                return;

            var result = await Frame.RunAsync("Changing the payer…", () => _service.Apply(batch, record, newPayer, oldPayer));
            if (result == null || Frame.HasMessage) return; // error already shown, nothing saved

            NewBox.Text = "";
            await RefreshCurrentAsync();
            Frame.ShowSuccess("Changed the payer to \"" + newPayer + "\" on " + result.Item1 + " receipt(s) and " + result.Item2
                              + " A/R allocation line(s) in " + which + ".");
        }
    }
}
