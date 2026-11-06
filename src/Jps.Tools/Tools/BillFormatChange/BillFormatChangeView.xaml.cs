using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.BillFormatChange
{
    public sealed class BillFormatChangeTool : IJurisTool
    {
        public string Id => "JPS.BILLFORMAT";
        public FrameworkElement CreateView(IToolContext context) => new BillFormatChangeView(context);
    }

    /// <summary>
    /// Changes the bill and/or prebill format on clients and/or matters (matter formats live on the matter's bill-to),
    /// optionally limited by billing timekeeper, originator and practice class, after a pre-report the user approves.
    /// Ported from "JPS - Bill-PreBill Format Change" with these fixes: the client originator filter used the billing
    /// timekeeper; "Matters only" with both formats produced broken SQL; updates ran row by row with no transaction (now two
    /// set-based updates in one transaction); an empty pre-report still offered "Accept Changes"; the report SQL was copied to
    /// the user's clipboard; the UtilityLog line was unescaped and culture-dependent. New: the pre-report also lists matters
    /// that will change only because they share a bill-to with a selected matter.
    /// </summary>
    public partial class BillFormatChangeView : UserControl
    {
        private static readonly LookupItem NoChange = new LookupItem(null, "No change", "");

        private readonly BillFormatChangeService _service;

        public BillFormatChangeView(IToolContext context)
        {
            _service = new BillFormatChangeService(context.Session);
            InitializeComponent();
            Loaded += async (_, __) =>
            {
                List<LookupItem> formats = null, tkprs = null, classes = null;
                var ok = await Frame.RunAsync("Loading formats and timekeepers…", () =>
                {
                    formats = _service.LoadFormats();
                    tkprs = _service.LoadTimekeepers();
                    classes = _service.LoadPracticeClasses();
                });
                if (!ok) return;

                foreach (var box in new[] { OldBillBox, NewBillBox, OldPreBox, NewPreBox })
                {
                    box.ItemsSource = new[] { NoChange }.Concat(formats).ToList();
                    box.SelectedIndex = 0;
                }
                BillTkprBox.ItemsSource = tkprs;
                OrigBox.ItemsSource = tkprs;
                PcBox.ItemsSource = classes;
                if (tkprs.Count > 0) { BillTkprBox.SelectedIndex = 0; OrigBox.SelectedIndex = 0; }
                if (classes.Count > 0) PcBox.SelectedIndex = 0;
                UseBillTkpr.IsEnabled = UseOrig.IsEnabled = tkprs.Count > 0;
                UsePc.IsEnabled = classes.Count > 0;

                if (formats.Count == 0)
                    Frame.ShowWarning("Juris has no bill formats set up, so there's nothing to change.");
                UpdateButton();
            };
        }

        private static LookupItem Picked(ComboBox box) => box.SelectedItem as LookupItem;
        private static string Format(ComboBox box) => Picked(box) == null || Picked(box) == NoChange ? null : Picked(box).Code;

        private void OnFilterToggled(object sender, RoutedEventArgs e)
        {
            if (UsePc == null) return; // during InitializeComponent
            // At most two filters at once (as the original): with two ticked, the third is disabled.
            var boxes = new[] { UseBillTkpr, UseOrig, UsePc };
            var ticked = boxes.Count(b => b.IsChecked == true);
            foreach (var b in boxes)
                if (b.IsChecked != true)
                    b.IsEnabled = ticked < 2 && (b == UsePc ? PcBox.Items.Count > 0 : BillTkprBox.Items.Count > 0);
            UpdateButton();
        }

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateButton();

        /// <summary>Returns the first thing stopping a run, or null when the inputs are complete.</summary>
        private string Problem()
        {
            string oldBill = Format(OldBillBox), newBill = Format(NewBillBox), oldPre = Format(OldPreBox), newPre = Format(NewPreBox);
            if (oldBill == null && oldPre == null) return "Pick at least one format to change.";
            if (oldBill != null && newBill == null) return "Pick the new bill format.";
            if (oldPre != null && newPre == null) return "Pick the new prebill format.";
            if (oldBill == null && newBill != null) return "Pick the bill format to change from, or set the new bill format back to \"No change\".";
            if (oldPre == null && newPre != null) return "Pick the prebill format to change from, or set the new prebill format back to \"No change\".";
            if (oldBill != null && oldBill == newBill) return "The new bill format is the same as the old one.";
            if (oldPre != null && oldPre == newPre) return "The new prebill format is the same as the old one.";
            if (UseBillTkpr.IsChecked == true && Picked(BillTkprBox) == null) return "Pick the billing timekeeper.";
            if (UseOrig.IsChecked == true && Picked(OrigBox) == null) return "Pick the originating timekeeper.";
            if (UsePc.IsChecked == true && Picked(PcBox) == null) return "Pick the practice class.";
            return null;
        }

        private void UpdateButton()
        {
            if (ReviewButton == null || NewPreBox == null) return; // during InitializeComponent
            var problem = Problem();
            FormatHint.Text = problem ?? "When you change both formats, only clients and bill-tos using both old formats are changed.";
            ReviewButton.IsEnabled = problem == null;
        }

        private FormatChangeCriteria Capture()
        {
            var filters = new List<string>();
            var c = new FormatChangeCriteria
            {
                Scope = ScopeClients.IsChecked == true ? FormatScope.ClientsOnly
                      : ScopeMatters.IsChecked == true ? FormatScope.MattersOnly : FormatScope.ClientsAndMatters,
                OldBill = Format(OldBillBox),
                OldPre = Format(OldPreBox),
            };
            c.NewBill = c.OldBill == null ? null : Format(NewBillBox);
            c.NewPre = c.OldPre == null ? null : Format(NewPreBox);
            if (UseBillTkpr.IsChecked == true)
            {
                c.BillingTkpr = System.Convert.ToInt32(Picked(BillTkprBox).Key);
                filters.Add("billing timekeeper " + Picked(BillTkprBox).Code);
            }
            if (UseOrig.IsChecked == true)
            {
                c.Originator = System.Convert.ToInt32(Picked(OrigBox).Key);
                filters.Add("originating timekeeper " + Picked(OrigBox).Code);
            }
            if (UsePc.IsChecked == true)
            {
                c.PracticeClass = Picked(PcBox).Code;
                filters.Add("practice class " + Picked(PcBox).Code);
            }
            c.FilterDescription = string.Join(", ", filters);
            return c;
        }

        private async void OnReview(object sender, RoutedEventArgs e)
        {
            var problem = Problem();
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return;
            }
            var criteria = Capture();

            var report = await Frame.RunAsync("Finding clients and matters…", () => _service.PreReport(criteria));
            if (report == null) return;
            if (report.Rows.Count == 0)
            {
                Frame.ShowWarning("Your selections returned no clients or matters. Change them and try again.");
                return;
            }

            var hasIncluded = report.Columns.Contains(BillFormatChangeService.IncludedColumn);
            var clients = criteria.Scope == FormatScope.ClientsOnly ? report.Rows.Count
                : report.Rows().Count(r => Sql.Text(r, BillFormatChangeService.IncludedColumn) == "Client");
            var matters = criteria.Scope == FormatScope.ClientsOnly ? 0 : report.Rows.Count - clients;
            var shared = hasIncluded ? report.Rows().Count(r => Sql.Text(r, BillFormatChangeService.IncludedColumn) == BillFormatChangeService.SharedBillToReason) : 0;

            var subtitle = Summary(criteria, clients, matters)
                           + (shared > 0 ? " " + shared + " of the matters are not in your selection but share a bill-to with a selected matter, so they change too." : "")
                           + (string.IsNullOrEmpty(criteria.FilterDescription) ? "" : " Filtered by " + criteria.FilterDescription + ".");
            if (!ReportWindow.Confirm(this, "Bill and prebill formats to change", report, subtitle, "Change formats"))
                return;

            var result = await Frame.RunAsync("Changing formats…", () => _service.Apply(criteria));
            if (result == null) return; // error already shown

            foreach (var box in new[] { OldBillBox, NewBillBox, OldPreBox, NewPreBox })
                box.SelectedIndex = 0;
            UpdateButton();
            Frame.ShowSuccess("Formats changed on " + result.Clients + " client(s) and " + result.BillTos + " matter bill-to(s), covering "
                              + matters + " matter(s).");
        }

        private static string Summary(FormatChangeCriteria c, int clients, int matters)
        {
            var what = new List<string>();
            if (c.ChangeBill) what.Add("bill format " + c.OldBill + " to " + c.NewBill);
            if (c.ChangePre) what.Add("prebill format " + c.OldPre + " to " + c.NewPre);
            var who = c.Scope == FormatScope.ClientsOnly ? clients + " client(s)"
                    : c.Scope == FormatScope.MattersOnly ? matters + " matter(s)"
                    : clients + " client(s) and " + matters + " matter(s)";
            return "Changes " + string.Join(" and ", what) + " on " + who + ".";
        }
    }
}
