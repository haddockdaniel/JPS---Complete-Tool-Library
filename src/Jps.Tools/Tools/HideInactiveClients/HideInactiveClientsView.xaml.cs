using System;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Jps.Core.Data;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.HideInactiveClients
{
    public sealed class HideInactiveClientsTool : IJurisTool
    {
        public string Id => "JPS.HIDEINACTCLI";
        public FrameworkElement CreateView(IToolContext context) => new HideInactiveClientsView(context);
    }

    /// <summary>
    /// Hides old clients with no open matters from the Clients folder of the Juris explorer (removes their DocumentTree
    /// reference node; no client or matter data changes), filtered by client opened date and optionally by originator.
    /// Ported from "JPS - Hide Inactive Clients" with these fixes: the default "All Originators" matched nobody, so it hid
    /// nothing; there was no confirmation, count or completion message (now a pre-report and counts); a node with children
    /// raised FK1_DocumentTree and crashed the tool (now only leaf reference nodes are removed, in one transaction); the date
    /// and timekeeper were concatenated into SQL as culture-dependent text; nothing was logged (now a UtilityLog
    /// line; the approved pre-report can be opened in Excel from the report window). The date label now says what is filtered: opened date.
    /// </summary>
    public partial class HideInactiveClientsView : UserControl
    {
        private static readonly LookupItem AllOriginators = new LookupItem(null, "All originating timekeepers", "");

        private readonly HideInactiveClientsService _service;

        public HideInactiveClientsView(IToolContext context)
        {
            _service = new HideInactiveClientsService(context.Session);
            InitializeComponent();
            OpenedPicker.SelectedDate = DateTime.Today;
            Loaded += async (_, __) =>
            {
                var originators = await Frame.RunAsync("Loading originating timekeepers…", () => _service.LoadOriginators());
                if (originators == null) return;
                OriginatorBox.ItemsSource = new[] { AllOriginators }.Concat(originators).ToList();
                OriginatorBox.SelectedIndex = 0;
                UpdateButton();
            };
        }

        private LookupItem Originator => OriginatorBox.SelectedItem as LookupItem;

        private void OnInputChanged(object sender, EventArgs e) => UpdateButton();

        private void UpdateButton()
        {
            if (HideButton == null) return; // during InitializeComponent
            HideButton.IsEnabled = OpenedPicker.SelectedDate != null && Originator != null;
        }

        private async void OnHide(object sender, RoutedEventArgs e)
        {
            var opened = OpenedPicker.SelectedDate;
            var originator = Originator;
            if (opened == null || originator == null)
            {
                Frame.ShowWarning("Pick a date and an originating timekeeper first.");
                return;
            }
            int? origKey = originator == AllOriginators ? (int?)null : Convert.ToInt32(originator.Key);
            var criteria = "opened on or before " + opened.Value.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture)
                           + ", originating timekeeper " + (origKey == null ? "any" : originator.Code);

            var found = await Frame.RunAsync("Finding inactive clients…", () => _service.Select(opened.Value, origKey));
            if (found == null) return;
            if (found.Rows.Count == 0)
            {
                Frame.ShowInfo("No clients match the selected criteria. Nothing was changed.");
                return;
            }

            var hideable = found.Rows().Where(r => Sql.Text(r, "Result") == HideInactiveClientsService.WillHide)
                .Select(r => Convert.ToInt32(r["CliSysNbr"])).ToList();
            var kept = found.Rows.Count - hideable.Count;
            var report = found.DefaultView.ToTable(false, "Client Code", "Client Name", "Date Opened", "Result");
            var subtitle = hideable.Count + " client(s) with no open matters will be hidden (" + criteria + ")."
                           + (kept > 0 ? " " + kept + " will be kept because items are filed under their Juris explorer entry." : "");

            if (hideable.Count == 0)
            {
                ReportWindow.Show(this, "Inactive clients", report, subtitle);
                Frame.ShowWarning("All " + kept + " matching client(s) have items filed under their Juris explorer entry, so none can be hidden. Nothing was changed.");
                return;
            }
            if (!ReportWindow.Confirm(this, "Inactive clients to hide", report, subtitle, "Hide " + hideable.Count + " client(s)"))
                return;

            var result = await Frame.RunAsync("Hiding clients…", () => _service.Hide(hideable, criteria));
            if (result == null) return; // error already shown, nothing deleted

            var notHidden = found.Rows.Count - result.ClientsHidden;
            var message = "Hid " + result.ClientsHidden + " client(s) from the Juris explorer (" + result.NodesHidden + " explorer entr" + (result.NodesHidden == 1 ? "y" : "ies") + " removed)."
                          + (notHidden > 0 ? " " + notHidden + " matching client(s) were kept." : "");
            if (notHidden > 0) Frame.ShowWarning(message); else Frame.ShowSuccess(message);
        }
    }
}
