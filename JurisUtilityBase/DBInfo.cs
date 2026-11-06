using System;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Gizmox.Data;
using Gizmox.WinTLB;
using JurisAuthenticator;
using JurisDbInfo;
using JurisUtilityBase.Properties;

namespace JurisUtilityBase
{
    public partial class DBInfo : Form
    {
        #region Private  members

        private readonly JurisUtility _jurisUtility;
        private const string Id1 = "\fnUGSQQ";
        private const string Id2 = @";qS";
        private const string Id3 = @"\`UD";
        private const string Id4 = @"Wasabi!";

        #endregion

        #region Public properties

        public string CompanyCode { get; set; }

        public string JurisDbName { get; set; }

        public string JBillsDbName { get; set; }

        public int FldClient { get; set; }

        public int FldMatter { get; set; }

        public string SqlVersion
        {
            get
            {
                var sqlVersion = GetSqlVersion();
                if (sqlVersion.StartsWith(@"Microsoft SQL Server  2000"))
                {
                    return "2000";
                }
                if (sqlVersion.StartsWith(@"Microsoft SQL Server 2005 "))
                {
                    return "2005";
                }
                if (sqlVersion.StartsWith(@"Microsoft SQL Server 2008 "))
                {
                    return "2008";
                }
                return "Unknown";
            }
        }

        #endregion

        #region Constructor

        public DBInfo()
        {
            InitializeComponent();
            _jurisUtility = new JurisUtility();
        }

        #endregion

        #region Public methods

        public void LoadCompanies()
        {
            var companies = _jurisUtility.Companies.Cast<object>().Cast<Instance>().ToList();
            listBoxCompanies.SelectedIndexChanged -= listBoxCompanies_SelectedIndexChanged;
            listBoxCompanies.ValueMember = "Code";
            listBoxCompanies.DisplayMember = "Key";
            listBoxCompanies.DataSource = companies;
            listBoxCompanies.SelectedIndexChanged += listBoxCompanies_SelectedIndexChanged;
            var defaultCompany = companies.FirstOrDefault(c => c.Default == Instance.JurisDefaultCompany.jdcJuris);
            if (companies.Count > 0)
            {
                listBoxCompanies.SelectedItem = defaultCompany ?? companies[0];
            }
        }

        #endregion

        #region MainForm events

        private void Form1_Load(object sender, EventArgs e)
        {
        }

        private void listBoxCompanies_SelectedIndexChanged(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            ClearDbInfo();
            if (_jurisUtility.DbOpen)
            {
                _jurisUtility.CloseDatabase();
            }
            CompanyCode = "Company" + listBoxCompanies.SelectedValue;
            _jurisUtility.SetInstance(CompanyCode);
            JurisDbName = _jurisUtility.Company.DatabaseName;
            JBillsDbName = "JBills" + _jurisUtility.Company.Code;
            UpdateStatus(@"Opening database...", 1, 2);
            _jurisUtility.OpenDatabase();
            if (_jurisUtility.DbOpen)
            {
                GetFieldLengths();
                ShowDbInfo();
            }
            else
            {
                UpdateStatus(@"Unable to open database.", 2, 2);
                toolStripStatusLabel.Text = @"Status: No open database";
            }
            this.Cursor = Cursors.Default;

        }

        private void buttonFirmName_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            UpdateStatus(@"Labelling Core Juris Window...", 1, 2);
            ApplyFirmName();
            ShowDbInfo();
            UpdateStatus(@"Database Info Confirmed.", 2, 2);
            this.Cursor = Cursors.Default;
        }

        private void buttonResetSmgrPwd_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            UpdateStatus(@"Resetting SMGR Password ...", 1, 3);
            ResetSmgrPassword();
            UpdateStatus(@"Acquiring SMGR Password ...", 2, 3);
            textBoxSmgrPassword.Text = GetSmgrPassword();
            UpdateStatus(@"Database Info Confirmed.", 3, 3);
            this.Cursor = Cursors.Default;
        }

        private void buttonResetLastBackup_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            toolStripStatusLabel.Text = @"Status: Working...";
            UpdateStatus(@"Resetting Last Backup...", 1, 3);
            ResetLastBackup();
            UpdateStatus(@"Acquiring Last Backup Date...", 2, 3);
            textBoxLastBackup.Text = GetLastBackup();
            UpdateStatus(@"Database Info Confirmed.", 3, 3);
            this.Cursor = Cursors.Default;
        }

        private void buttonResetUsers_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            if (checkBoxJurisRpt.Checked)
            {
                if (ResetJurisRpt())
                {
                    WriteLog(@"JurisDbInfo: Password and permissions reset for login JurisRPT.");
                }
                else
                {
                    MessageBox.Show(this, @"JurisRPT user reset failed.", @"SQL Error", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            if (checkBoxJurisRpt2.Checked)
            {
                if (ResetJurisRpt2())
                {
                    WriteLog(@"JurisDbInfo: Password and permissions reset for login JurisRPT2.");
                }
                else
                {
                    MessageBox.Show(this, @"JurisRPT2 user reset failed.", @"SQL Error", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            if (checkBoxJurisRo.Checked)
            {
                if (ResetJurisRo())
                {
                    WriteLog(@"JurisDbInfo: Password and permissions reset for login JurisRO.");
                }
                else
                {
                    MessageBox.Show(this, @"JurisRO user reset failed.", @"SQL Error", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            if (checkBoxAthensRo.Checked)
            {
                if (ResetAthensRo())
                {
                    WriteLog(@"JurisDbInfo: Password and permissions reset for login AthensRO.");
                }
                else
                {
                    MessageBox.Show(this, @"AthensRO user reset failed.", @"SQL Error", MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }

            if (checkBoxJurisBackup.Checked)
            {
                if (string.IsNullOrWhiteSpace(textBoxBackupPassword.Text))
                {
                    MessageBox.Show(this, @"JurisBackup password must not be blank.");
                }
                else
                {
                    if (ResetJurisBackup(textBoxBackupPassword.Text))
                    {
                        WriteLog(@"JurisDbInfo: Password and permissions reset for login JurisBackup.");
                    }
                    else
                    {
                        MessageBox.Show(this, @"JurisBackup user reset failed.", @"SQL Error", MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
            ShowDbInfo();
            this.Cursor = Cursors.Default;
        }

        private void buttonClearOnlineUsers_Click(object sender, EventArgs e)
        {
            this.Cursor = Cursors.WaitCursor;
            UpdateStatus(@"Clearing online flags...", 1, 3);
            ClearOnlineFlags();
            UpdateStatus(@"Aquiring online users...", 2, 3);
            textBoxUsersOnline.Text = GetOnlineUsers();
            UpdateStatus(@"Database Info Confirmed.", 3, 3);
            this.Cursor = Cursors.Default;
        }

        private void buttonExit_Click(object sender, EventArgs e)
        {
            if (_jurisUtility.DbOpen)
            {
                _jurisUtility.CloseDatabase();
            }
            Application.Exit();
        }

        #endregion

        #region Private methods

        private void ShowDbInfo()
        {
            try
            {
                this.Cursor = Cursors.WaitCursor;
                UpdateStatus(@"Acquiring Firm Name...", 1, 8);
                textBoxFirmName.Text = GetFirmName();
                UpdateStatus(@"Acquiring Juris Version ...", 2, 8);
                textBoxJurisVersion.Text = GetJurisVersion();
                UpdateStatus(@"Acquiring Juris Suite version...", 3, 8);
                textBoxJurisSuiteVersion.Text = GetJurisSuiteVersion();
                UpdateStatus(@"Acquiring Database Size...", 4, 8);
                textBoxDatabaseSize.Text = GetDatabaseSize();
                UpdateStatus(@"Acquiring SMGR Password...", 5, 8);
                textBoxSmgrPassword.Text = GetSmgrPassword();
                UpdateStatus(@"Acquiring Last Backup...", 6, 8);
                textBoxLastBackup.Text = GetLastBackup();
                UpdateStatus(@"Acquiring SQL Version...", 7, 8);
                textBoxSqlVersion.Text = GetSqlVersion();
                textBoxUsersOnline.Text = GetOnlineUsers();
            }
            finally
            {
                this.Cursor = Cursors.Default;
                UpdateStatus(@"Database Info Confirmed", 8, 8);
                toolStripStatusLabel.Text = @"Status: Ready to execute";
            }
        }

        private void ClearDbInfo()
        {
            textBoxFirmName.Text = string.Empty;
            textBoxJurisVersion.Text = string.Empty;
            textBoxJurisSuiteVersion.Text = string.Empty;
            textBoxDatabaseSize.Text = string.Empty;
            textBoxSmgrPassword.Text = string.Empty;
            textBoxLastBackup.Text = string.Empty;
            textBoxSqlVersion.Text = string.Empty;
            textBoxUsersOnline.Text = string.Empty;
            checkBoxAthensRo.Checked = false;
            checkBoxJurisBackup.Checked = false;
            checkBoxJurisRo.Checked = false;
            checkBoxJurisRpt.Checked = false;
            checkBoxJurisRpt2.Checked = false;
            textBoxBackupPassword.Text = string.Empty;
            this.Refresh();
        }

        private void GetFieldLengths()
        {
            try
            {
                var ds = _jurisUtility.ExecuteSqlCommand(0, Resources.GetFieldLengths);
                var row = ds.Tables["Table"].Rows[0];
                var regex = new Regex(@"\d+");
                var fldClient = row["FldClient"] as string;
                var fldMatter = row["FldMatter"] as string;
                FldClient = int.Parse(regex.Match(fldClient ?? "0").Value);
                FldMatter = int.Parse(regex.Match(fldMatter ?? "0").Value);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error getting field lengths", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private string GetComputerAndUser()
        {
            var computerName = Environment.MachineName;
            var windowsIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var userName = (windowsIdentity != null) ? windowsIdentity.Name : "Unknown";
            return computerName + "/" + userName;
        }

        private string GetFirmName()
        {
            var firmName = string.Empty;
            var sql = Resources.SqlGetFirmName;
            try
            {
                var ds = _jurisUtility.ExecuteSqlCommand(0, sql);
                firmName = ds.Tables["Table"].Rows[0]["FirmName"] as string;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error getting firm name", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return firmName;
        }

        private void ApplyFirmName()
        {
            try
            {
                var sql = string.Format(Resources.SqlSetFirmNameSysParam, textBoxFirmName.Text);
                if (_jurisUtility.ExecuteNonQueryCommand(0, sql) == 1)
                {
                    sql = Resources.SqlApplyFirmName;
                    _jurisUtility.ExecuteNonQueryCommand(0, sql);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error setting firm name", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private string GetJurisSuiteVersion()
        {
            string version;

            var sql = Resources.SqlGetJurisSuiteVersion;
            try
            {
                var ds = _jurisUtility.ExecuteSql(0, sql);
                version = ds.Tables["Table"].Rows[0]["RunningVersion"] as string;
            }
            catch (Exception)
            {
                version = "n/a";
            }

            return version;
        }

        private string GetJurisVersion()
        {
            string version = string.Empty;

            try
            {
                var ds = _jurisUtility.ExecuteSql(0, Resources.SqlGetRevSeq);
                var revSql = ds.Tables["Table"].Rows[0]["Rev"] as string ?? "0";
                var rev = int.Parse(revSql);
                switch (rev)
                {
                    case 324:
                        version = "Juris 2.05";
                        break;
                    case 382:
                        version = "Juris 2.1x";
                        break;
                    case 434:
                        version = "Juris 2.21";
                        break;
                    case 436:
                        version = "Juris 2.21sp1";
                        break;
                    case 480:
                        version = "Juris 2.25";
                        break;
                    case 483:
                        version = "Juris 2.25sp1";
                        break;
                    case 484:
                        version = "Juris 2.25sp2";
                        break;
                    case 509:
                        version = "Juris 2.30";
                        break;
                    case 510:
                        version = "Juris 2.30sp1/sp2";
                        break;
                    default:
                        ds = _jurisUtility.ExecuteSqlCommand(0, Resources.SqlGetJurisVersion);
                        var jv = ds.Tables["Table"].Rows[0]["CurJurisVersion"] as string;
                        switch (jv)
                        {
                            case "2.35.0.30":
                                version = "Juris 2.35 (2.35.0.30)";
                                break;
                            case "2.35.0.44":
                                version = "Juris 2.35sp1 (2.35.0.44)";
                                break;
                            default:
                                version = jv;
                                break;
                        }
                        break;
                }
            }
            catch (Exception)
            {
                
            }            

            return version;
        }

        private string GetDatabaseSize()
        {
            string dbSize;
            try
            {
                var ds = _jurisUtility.ExecuteSql(0, Resources.SqlGetDbSize);
                dbSize = ds.Tables["Table"].Rows[0]["database_size"] as string;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error getting database size.", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                dbSize = "error";
            }

            return dbSize;
        }

        private string GetSmgrPassword()
        {
            string smgrPassword;

            try
            {
                var ds = _jurisUtility.ExecuteSql(0, Resources.SqlGetSmgrPassword);
                var empPassword = ds.Tables["Table"].Rows[0]["EmpPassword"] as string;
                smgrPassword = empPassword == "smgr" ? "smgr" : _jurisUtility.Encrypt(empPassword, @"Athens");
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error getting SMGR password", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                smgrPassword = @"error";
            }

            return smgrPassword;
        }

        private void ResetSmgrPassword()
        {
            try
            {
                _jurisUtility.ExecuteNonQueryCommand(0, Resources.SqlResetSmgrPassword);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error resetting SMGR password", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);                
            }
        }

        private string GetLastBackup()
        {
            string lastBackup = @"No backups available";

            var lastBackupDate = GetLastBackupDate(JurisDbName);
            if (lastBackupDate != null)
            {
                var date = lastBackupDate.Value;
                lastBackup = string.Format(@"{0} {1}", date.ToShortDateString(), date.ToShortTimeString());
            }

            return lastBackup;
        }

        private void ResetLastBackup()
        {
            int recordsAffected = 0;
            int jurisBackupSetId = GetBackupSetId(JurisDbName);
            int jBillsBackupSetId = GetBackupSetId(JBillsDbName);
            _jurisUtility.BeginTransaction(2);
            try
            {
                string sql;
                if (jurisBackupSetId != -1)
                {
                    sql = string.Format(Resources.SqlUpdateBackupSet, JurisDbName, jurisBackupSetId);
                    recordsAffected = _jurisUtility.ExecuteNonQueryCommand(2, sql);
                }

                if (jBillsBackupSetId != -1)
                {
                    sql = string.Format(Resources.SqlUpdateBackupSet, JBillsDbName, jBillsBackupSetId);
                    recordsAffected += _jurisUtility.ExecuteNonQueryCommand(2, sql);
                }

                if (recordsAffected == 2)
                {
                    _jurisUtility.CommitTransaction(2);
                }
                else
                {
                    MessageBox.Show(this, @"You must have at least one backup of this database.", @"No backups found",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _jurisUtility.RollbackTransaction(2);
                }

            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error resetting last backup.", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                _jurisUtility.RollbackTransaction(2);
            }
        }

        private int GetBackupSetId(string databaseName)
        {
            int backupSetId;
            try
            {
                var sql = string.Format(Resources.SqlGetBackupSetId, databaseName);
                var ds = _jurisUtility.ExecuteSql(2, sql);
                backupSetId = (int)ds.Tables["Table"].Rows[0]["backup_set_id"];
            }
            catch (Exception)
            {
                backupSetId = -1;
            }

            return backupSetId;
        }

        private DateTime? GetLastBackupDate(string databaseName)
        {
            DateTime? lastBackupDate;
            var backupSetId = GetBackupSetId(databaseName);
            if (backupSetId != -1)
            {
                try
                {
                    var sql = string.Format(Resources.SqlGetLastBackup, JurisDbName, backupSetId);
                    var rsLastBackup = _jurisUtility.ExecuteSqlCommand(2, sql);
                    lastBackupDate = (DateTime)rsLastBackup.Tables["Table"].Rows[0]["LastBackup"];
                }
                catch (Exception)
                {
                    lastBackupDate = null;
                }
            }
            else
            {
                lastBackupDate = null;
            }
            return lastBackupDate;
        }

        private string GetSqlVersion()
        {
            string sqlVersion;

            try
            {
                var ds = _jurisUtility.ExecuteSqlCommand(2, Resources.SqlGetSqlVersion);
                var regEx = new Regex("^[^)]*[)]");
                sqlVersion = regEx.Match(ds.Tables["Table"].Rows[0]["Version"] as string ?? string.Empty).Value;
            }
            catch (Exception)
            {
                sqlVersion = @"Error";
            }

            return sqlVersion;
        }

        private string GetOnlineUsers()
        {
            string onlineUsers;

            try
            {
                var ds = _jurisUtility.ExecuteSqlCommand(0, Resources.SqlGetOnlineUsers);
                if (!ds.EOF())
                {
                    var users = (int)ds.Tables["Table"].Rows[0]["SysParamUsers"];
                    onlineUsers = users.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    onlineUsers = @"-error";
                }
            }
            catch (Exception)
            {
                onlineUsers = @"-error-";
            }

            return onlineUsers;
        }

        private void ClearOnlineFlags()
        {
            try
            {
                _jurisUtility.ExecuteNonQueryCommand(0, Resources.SqlUpdateCurWinUsers);
                _jurisUtility.ExecuteNonQueryCommand(0, Resources.SqlUpdateEmpOnline);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"Error clearing online flags", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private bool ResetJurisRpt()
        {
            try
            {
                UpdateStatus(@"Checking for JurisRPT...", 1, 7);
                var ds = GetSysLogins(@"JurisRPT");
                if (ds.EOF())
                {
                    UpdateStatus(@"Removing existing user JurisRPT...", 2, 7);
                    if (!RemoveUser(@"JurisRPT"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Creating login JurisRPT...", 3, 7);
                    CreateLogin("JurisRPT", string.Empty);
                    UpdateStatus(@"Creating user JurisRPT", 4, 7);
                    GrantDbAccess("JurisRPT");
                }
                else
                {
                    UpdateStatus(@"Removing existing user JurisRPT...", 2, 7);
                    if (!RemoveUser(@"JurisRPT"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Resetting password for JurisRPT", 3, 7);
                    SetSqlPassword(@"JurisRPT", string.Empty);
                    UpdateStatus(@"Creating user JurisRPT", 4, 7);
                    GrantDbAccess("JurisRPT");
                }

                UpdateStatus(@"Adding roles for JurisRPT...", 5, 7);
                var sql = @"EXECUTE sp_addsrvrolemember JurisRPT, dbcreator";
                _jurisUtility.ExecuteNonQueryCommand(2, sql);

                UpdateStatus(@"Adding roles for JurisRPT...", 6, 7);
                sql = @"EXECUTE sp_addrolemember 'db_datareader', 'JurisRPT'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);
                MessageBox.Show(this, @"User JurisRPT reset successfully", @"Success");
                UpdateStatus(@"JurisRPT User Reset", 7, 7);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"SQL Error Resetting JurisRPT", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

        }

        private bool ResetJurisRpt2()
        {
            try
            {
                UpdateStatus(@"Checking for JurisRPT2...", 1, 9);
                var ds = GetSysLogins(@"JurisRPT2");
                if (ds.EOF())
                {
                    UpdateStatus(@"Removing existing user JurisRPT2...", 2, 9);
                    if (!RemoveUser(@"JurisRPT2"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Creating login JurisRPT2...", 3, 9);
                    CreateLogin("JurisRPT2", _jurisUtility.Encrypt(Id2, Id4));
                    UpdateStatus(@"Creating user JurisRPT2", 4, 9);
                    GrantDbAccess("JurisRPT2");
                }
                else
                {
                    UpdateStatus(@"Removing existing user JurisRPT2...", 2, 9);
                    if (!RemoveUser(@"JurisRPT2"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Resetting password for JurisRPT2", 3, 9);
                    SetSqlPassword(@"JurisRPT2", _jurisUtility.Encrypt(Id2, Id4));
                    UpdateStatus(@"Creating user JurisRPT2", 4, 9);
                    GrantDbAccess("JurisRPT2");
                }

                UpdateStatus(@"Adding roles for JurisRPT2...", 5, 9);
                var sql = @"EXECUTE sp_addsrvrolemember JurisRPT2, dbcreator";
                _jurisUtility.ExecuteNonQueryCommand(2, sql);

                UpdateStatus(@"Adding roles for JurisRPT2...", 6, 9);
                sql = @"EXECUTE sp_addrolemember 'db_datareader', 'JurisRPT2'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                UpdateStatus(@"Adding roles for JurisRPT2...", 7, 9);
                sql = @"EXECUTE sp_addrolemember 'db_denydatawriter', 'JurisRPT2'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                UpdateStatus(@"Adding roles for JurisRPT2...", 8, 9);
                sql = @"EXECUTE sp_addrolemember 'db_backupoperator', 'JurisRPT2'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                MessageBox.Show(this, @"User JurisRPT2 reset successfully", @"Success");
                UpdateStatus(@"JurisRPT2 User Reset", 9, 9);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"SQL Error Resetting JurisRPT2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool ResetJurisRo()
        {
            try
            {
                UpdateStatus(@"Checking for JurisRO...", 1, 7);
                var ds = GetSysLogins(@"JurisRO");
                if (ds.EOF())
                {
                    UpdateStatus(@"Removing existing user JurisRO...", 2, 7);
                    if (!RemoveUser(@"JurisRO"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Creating login JurisRO...", 3, 7);
                    CreateLogin("JurisRO", _jurisUtility.Encrypt(Id3, Id4));
                    UpdateStatus(@"Creating user JurisRO", 4, 7);
                    GrantDbAccess("JurisRO");
                }
                else
                {
                    UpdateStatus(@"Removing existing user JurisRO...", 2, 7);
                    if (!RemoveUser(@"JurisRO"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Resetting password for JurisRO", 3, 7);
                    SetSqlPassword(@"JurisRO", _jurisUtility.Encrypt(Id3, Id4));
                    UpdateStatus(@"Creating user JurisRO", 4, 7);
                    GrantDbAccess("JurisRO");
                }

                UpdateStatus(@"Adding roles for JurisRO...", 5, 7);
                var sql = @"EXECUTE sp_addsrvrolemember JurisRO, dbcreator";
                _jurisUtility.ExecuteNonQueryCommand(2, sql);

                UpdateStatus(@"Adding roles for JurisRO...", 6, 7);
                sql = @"EXECUTE sp_addrolemember 'db_datareader', 'JurisRO'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);
                MessageBox.Show(this, @"User JurisRO reset successfully", @"Success");
                UpdateStatus(@"JurisRO User Reset", 7, 7);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"SQL Error Resetting JurisRO", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool ResetAthensRo()
        {
            try
            {
                UpdateStatus(@"Checking for AthensRO...", 1, 9);
                var ds = GetSysLogins(@"AthensRO");
                if (ds.EOF())
                {
                    UpdateStatus(@"Removing existing user AthensRO...", 2, 9);
                    if (!RemoveUser(@"AthensRO"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Creating login AthensRO...", 3, 9);
                    CreateLogin("AthensRO", _jurisUtility.Encrypt(Id1, Id4));
                    UpdateStatus(@"Creating user AthensRO", 4, 9);
                    GrantDbAccess("AthensRO");
                }
                else
                {
                    UpdateStatus(@"Removing existing user AthensRO...", 2, 9);
                    if (!RemoveUser(@"AthensRO"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Resetting password for AthensRO", 3, 9);
                    SetSqlPassword(@"AthensRO", _jurisUtility.Encrypt(Id1, Id4));
                    UpdateStatus(@"Creating user AthensRO", 4, 9);
                    GrantDbAccess("AthensRO");
                }

                UpdateStatus(@"Adding roles for AthensRO...", 5, 9);
                var sql = @"EXECUTE sp_addsrvrolemember AthensRO, dbcreator";
                _jurisUtility.ExecuteNonQueryCommand(2, sql);

                UpdateStatus(@"Adding roles for AthensRO...", 6, 9);
                sql = @"EXECUTE sp_addrolemember 'db_datareader', 'AthensRO'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                UpdateStatus(@"Adding roles for AthensRO...", 7, 9);
                sql = @"EXECUTE sp_addrolemember 'db_denydatawriter', 'AthensRO'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                UpdateStatus(@"Adding roles for AthensRO...", 8, 9);
                sql = @"EXECUTE sp_addrolemember 'db_backupoperator', 'AthensRO'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                MessageBox.Show(this, @"User AthensRO reset successfully", @"Success");
                UpdateStatus(@"AthensRO User Reset", 9, 9);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"SQL Error Resetting AthensRO", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool ResetJurisBackup(string password)
        {
            try
            {
                UpdateStatus(@"Checking for JurisBackup...", 1, 9);
                var ds = GetSysLogins(@"JurisBackup");
                if (ds.EOF())
                {
                    UpdateStatus(@"Removing existing user JurisBackup...", 2, 9);
                    if (!RemoveUser(@"JurisBackup"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Creating login JurisBackup...", 3, 9);
                    CreateLogin("JurisBackup", password);
                    UpdateStatus(@"Creating user JurisBackup", 4, 9);
                    GrantDbAccess("JurisBackup");
                }
                else
                {
                    UpdateStatus(@"Removing existing user JurisBackup...", 2, 9);
                    if (!RemoveUser(@"JurisBackup"))
                    {
                        return false;
                    }
                    UpdateStatus(@"Resetting password for JurisBackup", 3, 9);
                    SetSqlPassword(@"JurisBackup", password);
                    UpdateStatus(@"Creating user JurisBackup", 4, 9);
                    GrantDbAccess("JurisBackup");
                }

                UpdateStatus(@"Adding roles for JurisBackup...", 5, 9);
                var sql = @"EXECUTE sp_addsrvrolemember JurisBackup, dbcreator";
                _jurisUtility.ExecuteNonQueryCommand(2, sql);

                UpdateStatus(@"Adding roles for JurisBackup...", 6, 9);
                sql = @"EXECUTE sp_addrolemember 'db_denydatareader', 'JurisBackup'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                UpdateStatus(@"Adding roles for JurisBackup...", 7, 9);
                sql = @"EXECUTE sp_addrolemember 'db_denydatawriter', 'JurisBackup'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                UpdateStatus(@"Adding roles for JurisBackup...", 8, 9);
                sql = @"EXECUTE sp_addrolemember 'db_backupoperator', 'JurisBackup'";
                _jurisUtility.ExecuteNonQueryCommand(0, sql);
                _jurisUtility.ExecuteNonQueryCommand(1, sql);

                MessageBox.Show(this, @"User JurisBackup reset successfully", @"Success");
                UpdateStatus(@"JurisBackup User Reset", 9, 9);
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"SQL Error Resetting AthensRO", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool RemoveUser(string userName)
        {
            try
            {
                var sql = @"EXEC sp_helpuser";
                var dsDb = _jurisUtility.ExecuteSqlCommand(0, sql);
                var dsDb1 = _jurisUtility.ExecuteSqlCommand(1, sql);
                sql = string.Format(Resources.SqlRevokeDbAccess, userName);
                while (!dsDb.EOF())
                {
                    var row = dsDb.CurrentRow();
                    if (row["UserName"] as string == userName)
                    {
                        _jurisUtility.ExecuteSqlCommand(0, sql);
                        break;
                    }
                    dsDb.MoveNext();
                }

                while (!dsDb1.EOF())
                {
                    var row = dsDb1.CurrentRow();
                    if (row["UserName"] as string == userName)
                    {
                        _jurisUtility.ExecuteSqlCommand(1, sql);
                        break;
                    }
                    dsDb1.MoveNext();                    
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, @"SQL Error removing user", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
            return true;
        }

        private void CreateLogin(string userName, string password)
        {
            var sql = GetCreateLoginSql(userName, password);
            _jurisUtility.ExecuteNonQueryCommand(2, sql);
        }

        private string GetCreateLoginSql(string userName, string password)
        {
            if (SqlVersion == "2000")
            {
                return string.Format(Resources.Sql2000CreateLogin, userName, password);
            }
            return string.Format(Resources.SqlCreateLogin, userName, password);
        }

        private void GrantDbAccess(string userName)
        {
            var sql = string.Format(Resources.SqlGrantDbAccess, userName);
            _jurisUtility.ExecuteNonQueryCommand(0, sql);
            _jurisUtility.ExecuteNonQueryCommand(1, sql);
        }

        private string GetSetPasswordSql(string userName, string password)
        {
            if (SqlVersion == "2000")
            {
                return string.Format(Resources.Sql2000SetPassword, userName, password);
            }
            return string.Format(Resources.SqlSetPassword, userName, password);
        }

        private void SetSqlPassword(string userName, string password)
        {
            var sql = GetSetPasswordSql(userName, password);
            _jurisUtility.ExecuteNonQueryCommand(2, sql);
        }

        private string GetSysLoginsSql(string userName)
        {
            return string.Format(Resources.SqlGetSysLogins, userName);
        }

        private DataSet GetSysLogins(string userName)
        {
            return _jurisUtility.ExecuteSqlCommand(2, GetSysLoginsSql(userName));
        }

        private void WriteLog(string comment)
        {
            var sql =
                string.Format(Resources.SqlWriteLog,
                    DateTime.Now, GetComputerAndUser(), comment);
            _jurisUtility.ExecuteNonQueryCommand(0, sql);
        }

        /// <summary>
        /// Update status bar (text to display and step number of total completed)
        /// </summary>
        /// <param name="status">status text to display</param>
        /// <param name="step">steps completed</param>
        /// <param name="steps">total steps to be done</param>
        /// <param name="updateToolStrip">if <c>true</c> update the tool strip status text as well</param>
        private void UpdateStatus(string status, long step, long steps, bool updateToolStrip=true)
        {
            labelCurrentStatus.Text = status;
            if (status == @"Database Info Confirmed.")
            {
                toolStripStatusLabel.Text = @"Status: Ready to execute";
            }
            else if (updateToolStrip)
            {
                toolStripStatusLabel.Text = @"Status: " + status;
            }

            if (steps == 0)
            {
                progressBar.Value = 0;
                labelPercentComplete.Text = string.Empty;
            }
            else
            {
                double pctLong = Math.Round(((double)step/steps)*100.0);
                int percentage = (int)Math.Round(pctLong, 0);
                if ((percentage < 0) || (percentage > 100))
                {
                    progressBar.Value = 0;
                    labelPercentComplete.Text = string.Empty;
                }
                else
                {
                    progressBar.Value = percentage;
                    labelPercentComplete.Text = string.Format("{0} percent complete", percentage);
                }
            }
            this.Refresh();
            Application.DoEvents();
        }

        #endregion
    }
}
