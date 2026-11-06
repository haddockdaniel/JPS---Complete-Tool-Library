using System.Windows;
using System.Windows.Controls;
using Jps.Core.Modules;
using Jps.Tools.Common;

namespace Jps.Tools.Tools.FeeScheduleCopy
{
    public sealed class FeeScheduleCopyTool : IJurisTool
    {
        public string Id => "JPS.FEESCHEDCOPY";
        public FrameworkElement CreateView(IToolContext context) => new FeeScheduleCopyView(context);
    }

    /// <summary>
    /// Creates a new fee schedule from an existing active one, copying its timekeeper, personnel type and task code rates.
    /// Ported from "Fee Schedule Copy Utility" with these fixes: the explorer node id now comes from Juris'
    /// 'LastSysNbrDocTree' counter (Common\JurisCounters, which also advances it) instead of max(DTDocID)+1 (which left the counter behind and made Juris hit duplicate ids);
    /// the node key is the upper-cased code; everything runs in one transaction (no half-created schedules); rates are copied
    /// set-based so nothing is rounded to 2 decimals or broken by a non-US decimal separator; apostrophes in the description
    /// no longer break the SQL; an empty source list no longer crashes.
    /// </summary>
    public partial class FeeScheduleCopyView : UserControl
    {
        private readonly FeeScheduleCopyService _service;

        public FeeScheduleCopyView(IToolContext context)
        {
            _service = new FeeScheduleCopyService(context.Session);
            InitializeComponent();
            Loaded += async (_, __) => await LoadSourcesAsync();
        }

        private async System.Threading.Tasks.Task LoadSourcesAsync()
        {
            var sources = await Frame.RunAsync("Loading fee schedules…", () => _service.LoadSources());
            if (sources == null) return;
            SourceBox.ItemsSource = sources;
            if (sources.Count == 0)
                Frame.ShowWarning("Juris has no active fee schedules, so there's nothing to copy.");
            UpdateButton();
        }

        private string SourceCode => (SourceBox.SelectedItem as LookupItem)?.Code;
        private string NewCode => (CodeBox.Text ?? "").Trim().ToUpperInvariant();
        private string NewDesc => (DescBox.Text ?? "").Trim();

        private void OnInputChanged(object sender, RoutedEventArgs e) => UpdateButton();

        private void UpdateButton()
        {
            if (CopyButton == null) return; // during InitializeComponent
            var codeProblem = NewCode.Length == 0 ? null : FeeScheduleCopyService.CheckCodeFormat(NewCode);
            InputHint.Text = codeProblem ?? "The code can't contain spaces or quote marks. The new fee schedule is created as active.";
            CopyButton.IsEnabled = SourceCode != null && NewCode.Length > 0 && codeProblem == null && NewDesc.Length > 0;
        }

        private async void OnCopy(object sender, RoutedEventArgs e)
        {
            var from = SourceCode;
            var code = NewCode;
            var desc = NewDesc;
            if (from == null || code.Length == 0 || desc.Length == 0)
            {
                Frame.ShowWarning("Pick a fee schedule to copy and type a code and description for the new one.");
                return;
            }
            var problem = FeeScheduleCopyService.CheckCodeFormat(code);
            if (problem != null)
            {
                Frame.ShowWarning(problem);
                return;
            }

            RateCounts preview = null;
            var exists = false;
            var ok = await Frame.RunAsync("Checking " + code + "…", () =>
            {
                exists = _service.CodeExists(code);
                if (!exists) preview = _service.CountRates(from);
            });
            if (!ok) return;
            if (exists)
            {
                Frame.ShowWarning("Fee schedule " + code + " already exists. Pick a different code.");
                return;
            }

            if (!await Dialogs.ConfirmAsync("Create fee schedule " + code + "?",
                    "A new active fee schedule " + code + " (" + desc + ") will be created as a copy of " + from + ", with "
                    + preview + ".", "Create it"))
                return;

            var counts = await Frame.RunAsync("Creating fee schedule " + code + "…", () => _service.Copy(from, code, desc));
            if (counts == null) return; // error already shown

            SourceBox.SelectedItem = null;
            CodeBox.Text = "";
            DescBox.Text = "";
            await LoadSourcesAsync();
            Frame.ShowSuccess("Fee schedule " + code + " was created from " + from + " with " + counts + ".");
        }
    }
}
