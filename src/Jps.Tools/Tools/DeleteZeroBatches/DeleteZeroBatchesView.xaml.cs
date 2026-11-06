using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.DeleteZeroBatches
{
    public sealed class DeleteZeroBatches : IJurisTool
    {
        public string Id => "JPS.DELZEROBATCH";
        public FrameworkElement CreateView(IToolContext context) => new DeleteZeroBatchesView(context);
    }

    /// <summary>
    /// Removes empty batches (record count 0) of the chosen types from the Juris batch tree by deleting their DocumentTree
    /// leaves; batch header and detail rows are never deleted. Ported from "JPS - Delete Zero Count Batches v10" with these
    /// fixes: the journal entry type hid nothing once any A/R post batch had no JE link (NOT IN over a NULL); the
    /// pre-report did not match what Run did; there was no transaction; a stale record count alone could hide a batch that
    /// still had detail lines, and a tree node with children could be targeted. The original's unlinking of posted A/R
    /// post batches from their JE batches is no longer done, batches open in Juris are skipped, quick check batches are
    /// included with checks, and Run only hides batches the user approved in the pre-report.
    /// </summary>
    public partial class DeleteZeroBatchesView : UserControl
    {
        private readonly IToolContext _context;
        private JurisSession Session => _context.Session;

        public DeleteZeroBatchesView(IToolContext context)
        {
            _context = context;
            InitializeComponent();
            TypeList.ItemsSource = BatchType.LookupItems();
            UpdateButtons();
        }

        private List<BatchType> CheckedTypes => TypeList.Checked.Select(i => BatchType.FromKey(i.Key)).ToList();

        private void OnTypesChanged(object sender, EventArgs e)
        {
            Frame.ClearMessage();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var any = TypeList.Checked.Count > 0;
            HideButton.IsEnabled = any;
            PreviewButton.IsEnabled = any;
        }

        private async void OnPreview(object sender, RoutedEventArgs e)
        {
            var types = CheckedTypes;
            if (types.Count == 0) return;
            var table = await Frame.RunAsync("Finding empty batches…", () => new DeleteZeroBatchesService(Session).Preview(types));
            if (table == null) return;
            ReportWindow.Show(this, "Empty batches", table, Subtitle(table, types));
        }

        private async void OnHide(object sender, RoutedEventArgs e)
        {
            var types = CheckedTypes;
            if (types.Count == 0) return;
            var service = new DeleteZeroBatchesService(Session);
            var table = await Frame.RunAsync("Finding empty batches…", () => service.Preview(types));
            if (table == null) return;
            if (table.Rows.Count == 0)
            {
                Frame.ShowInfo("There are no empty batches of the selected types in the batch tree. Nothing was changed.");
                return;
            }

            var count = table.Rows.Count;
            if (!ReportWindow.Confirm(this, "Hide " + count.ToString("N0") + " empty batch(es)?", table,
                    Subtitle(table, types) + " They will no longer appear in the batch tree in Juris. This can't be undone from here.",
                    "Hide " + count.ToString("N0") + " batch(es)"))
                return;

            var approved = types.ToDictionary(t => t, t => table.Rows()
                .Where(r => Convert.ToString(r[DeleteZeroBatchesService.BatchTypeColumn]) == t.ReportLabel)
                .Select(r => Convert.ToInt32(r[DeleteZeroBatchesService.BatchNumberColumn])).ToList());
            IReadOnlyDictionary<BatchType, List<int>> work = approved;

            var hidden = await Frame.RunAsync("Hiding empty batches…", () => (int?)service.Hide(work));
            if (hidden == null) return; // error already shown
            if (hidden == 0)
                Frame.ShowWarning("None of those batches could be hidden; they changed since the list was made (for example someone opened or added to them). Nothing was changed.");
            else if (hidden < count)
                Frame.ShowSuccess("The process is complete. " + hidden.Value.ToString("N0") + " of " + count.ToString("N0")
                    + " batch(es) are hidden from the batch tree; the rest changed since the list was made and were left alone.");
            else
                Frame.ShowSuccess("The process is complete. " + hidden.Value.ToString("N0") + " empty batch(es) are hidden from the batch tree.");
        }

        private static string Subtitle(DataTable table, IEnumerable<BatchType> types) =>
            table.Rows.Count.ToString("N0") + " empty batch(es) of type: " + string.Join(", ", types.Select(t => t.Label)) + ".";
    }
}
