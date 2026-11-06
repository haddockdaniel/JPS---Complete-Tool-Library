using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;
using Jps.Tools.Tools.DeleteZeroBatches;

namespace Jps.Tools.Tools.HideOldBatches
{
    public sealed class HideOldBatches : IJurisTool
    {
        public string Id => "JPS.HIDEOLDBATCH";
        public FrameworkElement CreateView(IToolContext context) => new HideOldBatchesView(context);
    }

    /// <summary>
    /// Removes finished batches of the chosen types from the Juris batch tree when they sit in an accounting-period folder
    /// that ended before the chosen date, then removes period/user folders left empty; only DocumentTree changes. Ported
    /// from "JPS - Hide Old Batches by Date v10" with these fixes: the date was compared including the time of day, so the
    /// chosen month itself was hidden; a non-numeric folder setting crashed with a SQL conversion error; there was no
    /// transaction. Also changed for safety: unposted (or open) batches are never hidden, a period counts as old only
    /// when its real end date from ActngPeriod is before the cut-off (so mid-month dates, non-calendar fiscal years and
    /// period 13 work), and the user sees and approves the list of batches before anything is hidden.
    /// </summary>
    public partial class HideOldBatchesView : UserControl
    {
        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;

        public HideOldBatchesView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            TypeList.ItemsSource = BatchType.LookupItems();
            CutoffPicker.SelectedDate = DateTime.Today;
            UpdateButtons();
        }

        private List<BatchType> CheckedTypes => TypeList.Checked.Select(i => BatchType.FromKey(i.Key)).ToList();

        private void OnTypesChanged(object sender, EventArgs e) => OnInputChanged(sender, null);

        private void OnInputChanged(object sender, SelectionChangedEventArgs e)
        {
            Frame?.ClearMessage();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (HideButton == null || TypeList == null) return; // still loading
            var ok = TypeList.Checked.Count > 0 && CutoffPicker.SelectedDate != null;
            HideButton.IsEnabled = ok;
            PreviewButton.IsEnabled = ok;
        }

        private string Subtitle(HideOldBatchesService.Plan plan, DateTime cutoff, IEnumerable<BatchType> types) =>
            "Periods that ended before " + cutoff.ToString("MM/dd/yyyy") + "; types: " + string.Join(", ", types.Select(t => t.Label)) + ". "
            + plan.HideCount.ToString("N0") + " batch(es) will be hidden"
            + (plan.KeepCount > 0 ? ", " + plan.KeepCount.ToString("N0") + " stay because they aren't finished." : ".");

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var types = CheckedTypes;
            var date = CutoffPicker.SelectedDate;
            if (types.Count == 0 || date == null) return;
            var cutoff = date.Value.Date;
            var plan = await Frame.RunAsync("Finding old batches…", () => new HideOldBatchesService(Session).BuildPlan(types, cutoff));
            if (plan == null) return;
            ReportWindow.Show(this, "Old batches", plan.Report, Subtitle(plan, cutoff, types));
        }

        private async void OnHide(object sender, RoutedEventArgs e)
        {
            var types = CheckedTypes;
            var date = CutoffPicker.SelectedDate;
            if (types.Count == 0 || date == null) return;
            var cutoff = date.Value.Date;
            var service = new HideOldBatchesService(Session);
            var plan = await Frame.RunAsync("Finding old batches…", () => service.BuildPlan(types, cutoff));
            if (plan == null) return;
            if (plan.HideCount == 0)
            {
                Frame.ShowInfo(plan.Report.Rows.Count == 0
                    ? "There are no batches of the selected types in periods that ended before " + cutoff.ToString("MM/dd/yyyy") + ". Nothing was changed."
                    : "None of the batches in those periods are finished (posted), so none can be hidden. Use Preview batches to see them. Nothing was changed.");
                return;
            }

            var toHide = plan.Report.Clone();
            foreach (var row in plan.Report.Rows().Where(r => (string)r[HideOldBatchesService.ResultColumn] == HideOldBatchesService.WillHide))
                toHide.ImportRow(row);
            toHide.Columns.Remove(HideOldBatchesService.ResultColumn);
            if (!ReportWindow.Confirm(this, "Hide " + plan.HideCount.ToString("N0") + " old batch(es)?", toHide,
                    Subtitle(plan, cutoff, types) + " Period and user folders left empty are removed too. This can't be undone from here.",
                    "Hide " + plan.HideCount.ToString("N0") + " batch(es)"))
                return;

            var result = await Frame.RunAsync("Hiding old batches…", () => service.Hide(plan, types, cutoff));
            if (result == null) return; // error already shown
            if (result.Item1 == 0)
                Frame.ShowWarning("None of those batches could be hidden; they changed since the list was made. Nothing was changed.");
            else
                Frame.ShowSuccess("The tool finished without error. " + result.Item1.ToString("N0") + " batch(es) and "
                    + result.Item2.ToString("N0") + " empty folder(s) are hidden from the batch tree"
                    + (result.Item1 < plan.HideCount ? "; the rest changed since the list was made and were left alone." : "."));
        }
    }
}
