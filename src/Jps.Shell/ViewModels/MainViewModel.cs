using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Jps.Core.Catalog;
using Jps.Core.Data;
using Jps.Core.Licensing;
using Jps.Core.Modules;
using Wpf.Ui.Controls;

namespace Jps.Shell.ViewModels
{
    public enum ConnectionState { NoCompanies, Connecting, Connected, Failed }

    public sealed class NavItem : ObservableObject
    {
        private bool _isExpanded;

        public string Title { get; set; }
        public SymbolRegular Icon { get; set; }
        public bool IsHeader { get; set; }
        public bool IsLocked { get; set; }
        public ToolDefinition Tool { get; set; }
        public bool IsTool => Tool != null;
        public string Target { get; set; }
        /// <summary>For a tool: its group header. Tools are hidden while their header is collapsed.</summary>
        public NavItem Header { get; set; }
        /// <summary>For a header: e.g. "2 of 7" licensed.</summary>
        public string Detail { get; set; }

        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    }

    public sealed class MainViewModel : ObservableObject
    {
        private readonly LicenseService _license = new LicenseService();
        private readonly IReadOnlyDictionary<string, IJurisTool> _modules;
        private CompanyInfo _selectedCompany;
        private JurisSession _session;
        private ConnectionState _state;
        private string _connectionText;
        private string _backupText;
        private object _currentPage;
        private NavItem _selectedNav;
        private bool _isNavCompact;
        private bool _navigating;
        // Groups start collapsed; remembered across rebuilds (company switch, license import).
        private readonly HashSet<string> _expandedGroups = new HashSet<string>();

        public MainViewModel()
        {
            _modules = ModuleLoader.LoadAll(typeof(Jps.Tools.Common.ToolFrame).Assembly);
            _license.Load();
            GoHomeCommand = new RelayCommand(() => SelectNav("home"));
            OpenLicenseCommand = new RelayCommand(() => SelectNav("license"));
            BuildNav();
            LoadCompanies();
            SelectNav("home");
        }

        public ObservableCollection<CompanyInfo> Companies { get; } = new ObservableCollection<CompanyInfo>();
        public ObservableCollection<NavItem> NavItems { get; } = new ObservableCollection<NavItem>();
        public ICommand GoHomeCommand { get; }
        public ICommand OpenLicenseCommand { get; }

        public CompanyInfo SelectedCompany
        {
            get => _selectedCompany;
            set { if (Set(ref _selectedCompany, value) && value != null) _ = ConnectAsync(value); }
        }

        public ConnectionState State { get => _state; private set => Set(ref _state, value); }
        public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
        public string BackupText { get => _backupText; private set => Set(ref _backupText, value); }
        public string DatabaseText => _session == null ? "Not connected" : "Juris database: " + _session.JurisDbName;

        public string LicenseText => _license.IsValid
            ? "License: " + _license.License.Firm + " · " + _license.License.Modules.Count + " of " + ToolCatalog.All.Count + " tools"
            : "No valid license · all tools locked";

        public string VersionText => "v" + typeof(MainViewModel).Assembly.GetName().Version.ToString(3);

        public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

        public bool IsNavCompact { get => _isNavCompact; set => Set(ref _isNavCompact, value); }

        public NavItem SelectedNav
        {
            get => _selectedNav;
            set
            {
                if (value != null && value.IsHeader)
                {
                    // Clicking a group header expands/collapses it; the selection stays on the current page.
                    value.IsExpanded = !value.IsExpanded;
                    if (value.IsExpanded) _expandedGroups.Add(value.Title); else _expandedGroups.Remove(value.Title);
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(SelectedNav))));
                    return;
                }
                if (value == null || !Set(ref _selectedNav, value))
                    return;
                if (value.Header != null && !value.Header.IsExpanded)
                {
                    value.Header.IsExpanded = true;
                    _expandedGroups.Add(value.Header.Title);
                }
                Navigate(value);
            }
        }

        /// <summary>Opens "home", "license" or a tool id.</summary>
        public void SelectNav(string target) => SelectedNav = NavItems.First(n => n.Target == target);

        private void OpenTool(ToolDefinition tool) => SelectNav(tool.Id);

        private void Navigate(NavItem item)
        {
            if (_navigating)
                return;
            _navigating = true;
            try
            {
                switch (item.Target)
                {
                    case "home":
                        var cards = ToolCatalog.All
                            .Select(t => new ToolCard(t, _license.AccessFor(t.Id, _session), new RelayCommand(() => OpenTool(t))))
                            .ToList();
                        CurrentPage = new HomePage(cards, cards.Count(c => c.IsLicensed), LicenseWarning());
                        break;
                    case "license":
                        CurrentPage = new LicensePage(_license, _session, LicenseReloaded);
                        break;
                    default:
                        var access = _license.AccessFor(item.Tool.Id, _session);
                        _modules.TryGetValue(item.Tool.Id, out var module);
                        CurrentPage = access == ToolAccess.Licensed
                            ? new ToolPage(item.Tool, _session, module)
                            : (object)new LockedPage(item.Tool, access, _license, _session);
                        break;
                }
            }
            finally
            {
                _navigating = false;
            }
        }

        private string LicenseWarning()
        {
            if (!_license.IsValid)
                return _license.Problem + " Import your license from the License page.";
            if (_session != null && !_license.MatchesFirm(_session))
                return "Your license is for " + _license.License.Firm + " (" + string.Join(", ", _license.License.Databases) + "), not "
                     + _session.FirmName + " (" + _session.JurisDbName + "). Pick the licensed company, or contact JPS.";
            return null;
        }

        private void BuildNav()
        {
            var target = _selectedNav?.Target ?? "home";
            NavItems.Clear();
            NavItems.Add(new NavItem { Title = "All tools", Icon = SymbolRegular.Apps24, Target = "home" });
            foreach (var group in ToolCatalog.All.GroupBy(t => t.Group).OrderBy(g => Array.IndexOf(ToolCatalog.GroupOrder, g.Key)))
            {
                var licensed = group.Count(t => _license.AccessFor(t.Id, _session) == ToolAccess.Licensed);
                var header = new NavItem
                {
                    Title = group.Key,
                    IsHeader = true,
                    Detail = licensed + " of " + group.Count(),
                    IsExpanded = _expandedGroups.Contains(group.Key),
                };
                NavItems.Add(header);
                foreach (var tool in group)
                {
                    NavItems.Add(new NavItem
                    {
                        Title = tool.Name,
                        Icon = Icons.Parse(tool.Icon),
                        Tool = tool,
                        Target = tool.Id,
                        Header = header,
                        IsLocked = _license.AccessFor(tool.Id, _session) != ToolAccess.Licensed,
                    });
                }
            }
            NavItems.Add(new NavItem { Title = "License", Icon = SymbolRegular.Key24, Target = "license" });
            _selectedNav = null;
            SelectNav(NavItems.Any(n => n.Target == target) ? target : "home");
        }

        private void LicenseReloaded()
        {
            OnPropertyChanged(nameof(LicenseText));
            BuildNav();
        }

        private void LoadCompanies()
        {
            try
            {
                var companies = CompanyInfo.LoadAll();
                foreach (var company in companies)
                    Companies.Add(company);
                if (companies.Count == 0)
                {
                    State = ConnectionState.NoCompanies;
                    ConnectionText = "No Juris companies on this PC";
                    return;
                }
                // Support/testing: "JPS Tool Library.exe --company Juris9999002" opens on that company's database.
                var args = Environment.GetCommandLineArgs();
                var i = Array.IndexOf(args, "--company");
                var wanted = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                SelectedCompany = companies.FirstOrDefault(c => wanted != null && string.Equals(c.DatabaseName, wanted, StringComparison.OrdinalIgnoreCase))
                                  ?? companies.FirstOrDefault(c => c.IsDefault) ?? companies[0];
            }
            catch (Exception ex)
            {
                // JurisAuthenticator throws if Juris isn't installed on this workstation.
                State = ConnectionState.NoCompanies;
                ConnectionText = "Juris isn't installed on this PC";
                BackupText = ex.Message;
            }
        }

        private async Task ConnectAsync(CompanyInfo company)
        {
            _session?.Dispose();
            _session = null;
            State = ConnectionState.Connecting;
            ConnectionText = "Connecting…";
            BackupText = null;
            OnPropertyChanged(nameof(DatabaseText));
            BuildNav();

            try
            {
                var session = await Task.Run(() => JurisSession.Open(company));
                if (company != _selectedCompany)
                {
                    session.Dispose();
                    return;
                }
                _session = session;
                State = ConnectionState.Connected;
                ConnectionText = string.IsNullOrWhiteSpace(session.FirmName) ? "Connected" : session.FirmName;
                OnPropertyChanged(nameof(DatabaseText));
                BuildNav();
                BackupText = await Task.Run(() => DescribeBackup(session));
            }
            catch (Exception ex) when (ex is System.Data.SqlClient.SqlException || ex is InvalidOperationException)
            {
                if (company != _selectedCompany)
                    return;
                State = ConnectionState.Failed;
                ConnectionText = "Couldn't connect";
                BackupText = ex.Message;
            }
        }

        private static string DescribeBackup(JurisSession session)
        {
            try
            {
                var last = session.GetLastBackupDate();
                if (last == null)
                    return "Last backup: unknown";
                var days = (DateTime.Now - last.Value).TotalDays;
                return "Last backup: " + last.Value.ToString("g") + (days > 1 ? "  ·  back up before running a tool" : "");
            }
            catch (System.Data.SqlClient.SqlException)
            {
                return "Last backup: not available";
            }
        }
    }
}
