namespace JurisUtilityBase
{
    partial class DBInfo
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(DBInfo));
            this.JurisLogoImageBox = new System.Windows.Forms.PictureBox();
            this.LexisNexisLogoPictureBox = new System.Windows.Forms.PictureBox();
            this.statusStrip = new System.Windows.Forms.StatusStrip();
            this.toolStripStatusLabel = new System.Windows.Forms.ToolStripStatusLabel();
            this.listBoxCompanies = new System.Windows.Forms.ListBox();
            this.labelDescription = new System.Windows.Forms.Label();
            this.statusGroupBox = new System.Windows.Forms.GroupBox();
            this.labelCurrentStatus = new System.Windows.Forms.Label();
            this.progressBar = new System.Windows.Forms.ProgressBar();
            this.labelPercentComplete = new System.Windows.Forms.Label();
            this.buttonExit = new System.Windows.Forms.Button();
            this.labelFirmName = new System.Windows.Forms.Label();
            this.textBoxFirmName = new System.Windows.Forms.TextBox();
            this.buttonFirmName = new System.Windows.Forms.Button();
            this.toolTipMain = new System.Windows.Forms.ToolTip(this.components);
            this.buttonResetSmgrPwd = new System.Windows.Forms.Button();
            this.buttonResetLastBackup = new System.Windows.Forms.Button();
            this.checkBoxJurisRpt = new System.Windows.Forms.CheckBox();
            this.checkBoxJurisRpt2 = new System.Windows.Forms.CheckBox();
            this.checkBoxJurisRo = new System.Windows.Forms.CheckBox();
            this.buttonResetUsers = new System.Windows.Forms.Button();
            this.textBoxBackupPassword = new System.Windows.Forms.TextBox();
            this.textBoxUsersOnline = new System.Windows.Forms.TextBox();
            this.labelUsersOnline = new System.Windows.Forms.Label();
            this.buttonClearOnlineUsers = new System.Windows.Forms.Button();
            this.textBoxJurisVersion = new System.Windows.Forms.TextBox();
            this.labelJurisVersion = new System.Windows.Forms.Label();
            this.textBoxJurisSuiteVersion = new System.Windows.Forms.TextBox();
            this.labelJurisSuiteVersion = new System.Windows.Forms.Label();
            this.textBoxDatabaseSize = new System.Windows.Forms.TextBox();
            this.labelDatabaseSize = new System.Windows.Forms.Label();
            this.textBoxSmgrPassword = new System.Windows.Forms.TextBox();
            this.labelSmgrPassword = new System.Windows.Forms.Label();
            this.textBoxLastBackup = new System.Windows.Forms.TextBox();
            this.labelLastBackup = new System.Windows.Forms.Label();
            this.textBoxSqlVersion = new System.Windows.Forms.TextBox();
            this.labelSqlVersion = new System.Windows.Forms.Label();
            this.labelDbUsers = new System.Windows.Forms.Label();
            this.checkBoxAthensRo = new System.Windows.Forms.CheckBox();
            this.checkBoxJurisBackup = new System.Windows.Forms.CheckBox();
            this.labelBackupPassword = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.JurisLogoImageBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.LexisNexisLogoPictureBox)).BeginInit();
            this.statusStrip.SuspendLayout();
            this.statusGroupBox.SuspendLayout();
            this.SuspendLayout();
            // 
            // JurisLogoImageBox
            // 
            this.JurisLogoImageBox.Image = ((System.Drawing.Image)(resources.GetObject("JurisLogoImageBox.Image")));
            this.JurisLogoImageBox.InitialImage = ((System.Drawing.Image)(resources.GetObject("JurisLogoImageBox.InitialImage")));
            this.JurisLogoImageBox.Location = new System.Drawing.Point(0, 1);
            this.JurisLogoImageBox.Name = "JurisLogoImageBox";
            this.JurisLogoImageBox.Size = new System.Drawing.Size(104, 336);
            this.JurisLogoImageBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.AutoSize;
            this.JurisLogoImageBox.TabIndex = 0;
            this.JurisLogoImageBox.TabStop = false;
            // 
            // LexisNexisLogoPictureBox
            // 
            this.LexisNexisLogoPictureBox.Image = ((System.Drawing.Image)(resources.GetObject("LexisNexisLogoPictureBox.Image")));
            this.LexisNexisLogoPictureBox.Location = new System.Drawing.Point(8, 340);
            this.LexisNexisLogoPictureBox.Name = "LexisNexisLogoPictureBox";
            this.LexisNexisLogoPictureBox.Size = new System.Drawing.Size(96, 28);
            this.LexisNexisLogoPictureBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.AutoSize;
            this.LexisNexisLogoPictureBox.TabIndex = 1;
            this.LexisNexisLogoPictureBox.TabStop = false;
            // 
            // statusStrip
            // 
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripStatusLabel});
            this.statusStrip.Location = new System.Drawing.Point(0, 391);
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.Size = new System.Drawing.Size(658, 22);
            this.statusStrip.TabIndex = 2;
            // 
            // toolStripStatusLabel
            // 
            this.toolStripStatusLabel.BackColor = System.Drawing.SystemColors.Control;
            this.toolStripStatusLabel.ForeColor = System.Drawing.SystemColors.ControlText;
            this.toolStripStatusLabel.Name = "toolStripStatusLabel";
            this.toolStripStatusLabel.Size = new System.Drawing.Size(134, 17);
            this.toolStripStatusLabel.Text = "Status: Ready to Execute";
            // 
            // listBoxCompanies
            // 
            this.listBoxCompanies.FormattingEnabled = true;
            this.listBoxCompanies.Location = new System.Drawing.Point(111, 1);
            this.listBoxCompanies.Name = "listBoxCompanies";
            this.listBoxCompanies.Size = new System.Drawing.Size(262, 56);
            this.listBoxCompanies.TabIndex = 0;
            this.listBoxCompanies.SelectedIndexChanged += new System.EventHandler(this.listBoxCompanies_SelectedIndexChanged);
            // 
            // labelDescription
            // 
            this.labelDescription.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
            this.labelDescription.Font = new System.Drawing.Font("Arial", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelDescription.ForeColor = System.Drawing.SystemColors.ControlDarkDark;
            this.labelDescription.Location = new System.Drawing.Point(380, 1);
            this.labelDescription.Name = "labelDescription";
            this.labelDescription.Size = new System.Drawing.Size(268, 56);
            this.labelDescription.TabIndex = 1;
            this.labelDescription.Text = "DBInfo is for INTERNAL USE ONLY. Do not distribute or copy onto a customer system" +
    ".";
            // 
            // statusGroupBox
            // 
            this.statusGroupBox.Controls.Add(this.labelCurrentStatus);
            this.statusGroupBox.Controls.Add(this.progressBar);
            this.statusGroupBox.Controls.Add(this.labelPercentComplete);
            this.statusGroupBox.Location = new System.Drawing.Point(111, 59);
            this.statusGroupBox.Name = "statusGroupBox";
            this.statusGroupBox.Size = new System.Drawing.Size(542, 73);
            this.statusGroupBox.TabIndex = 5;
            this.statusGroupBox.TabStop = false;
            this.statusGroupBox.Text = "Utility Status:";
            // 
            // labelCurrentStatus
            // 
            this.labelCurrentStatus.AutoSize = true;
            this.labelCurrentStatus.Location = new System.Drawing.Point(7, 50);
            this.labelCurrentStatus.Name = "labelCurrentStatus";
            this.labelCurrentStatus.Size = new System.Drawing.Size(77, 13);
            this.labelCurrentStatus.TabIndex = 2;
            this.labelCurrentStatus.Text = "Current Status:";
            // 
            // progressBar
            // 
            this.progressBar.Location = new System.Drawing.Point(7, 27);
            this.progressBar.Name = "progressBar";
            this.progressBar.Size = new System.Drawing.Size(519, 20);
            this.progressBar.TabIndex = 0;
            // 
            // labelPercentComplete
            // 
            this.labelPercentComplete.Location = new System.Drawing.Point(399, 11);
            this.labelPercentComplete.Name = "labelPercentComplete";
            this.labelPercentComplete.Size = new System.Drawing.Size(127, 13);
            this.labelPercentComplete.TabIndex = 0;
            this.labelPercentComplete.Text = "% Complete";
            this.labelPercentComplete.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // buttonExit
            // 
            this.buttonExit.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.buttonExit.BackColor = System.Drawing.SystemColors.Window;
            this.buttonExit.BackgroundImage = ((System.Drawing.Image)(resources.GetObject("buttonExit.BackgroundImage")));
            this.buttonExit.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.buttonExit.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.buttonExit.FlatAppearance.BorderSize = 0;
            this.buttonExit.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.buttonExit.Location = new System.Drawing.Point(592, 345);
            this.buttonExit.Name = "buttonExit";
            this.buttonExit.Size = new System.Drawing.Size(61, 43);
            this.buttonExit.TabIndex = 3;
            this.buttonExit.TabStop = false;
            this.buttonExit.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.buttonExit.UseVisualStyleBackColor = false;
            this.buttonExit.Click += new System.EventHandler(this.buttonExit_Click);
            // 
            // labelFirmName
            // 
            this.labelFirmName.BackColor = System.Drawing.SystemColors.Control;
            this.labelFirmName.Location = new System.Drawing.Point(118, 139);
            this.labelFirmName.Name = "labelFirmName";
            this.labelFirmName.Size = new System.Drawing.Size(132, 18);
            this.labelFirmName.TabIndex = 6;
            this.labelFirmName.Text = "Firm Name:";
            // 
            // textBoxFirmName
            // 
            this.textBoxFirmName.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxFirmName.Location = new System.Drawing.Point(256, 139);
            this.textBoxFirmName.Name = "textBoxFirmName";
            this.textBoxFirmName.Size = new System.Drawing.Size(316, 20);
            this.textBoxFirmName.TabIndex = 7;
            this.textBoxFirmName.Text = "Your Firm Name Goes Here";
            // 
            // buttonFirmName
            // 
            this.buttonFirmName.Location = new System.Drawing.Point(578, 137);
            this.buttonFirmName.Name = "buttonFirmName";
            this.buttonFirmName.Size = new System.Drawing.Size(75, 23);
            this.buttonFirmName.TabIndex = 8;
            this.buttonFirmName.Text = "Apply";
            this.toolTipMain.SetToolTip(this.buttonFirmName, "Sets the main Core Juris window title to Firm Name (helpful when using multiple c" +
        "ompanies)");
            this.buttonFirmName.UseVisualStyleBackColor = true;
            this.buttonFirmName.Click += new System.EventHandler(this.buttonFirmName_Click);
            // 
            // buttonResetSmgrPwd
            // 
            this.buttonResetSmgrPwd.Location = new System.Drawing.Point(578, 241);
            this.buttonResetSmgrPwd.Name = "buttonResetSmgrPwd";
            this.buttonResetSmgrPwd.Size = new System.Drawing.Size(75, 23);
            this.buttonResetSmgrPwd.TabIndex = 17;
            this.buttonResetSmgrPwd.Text = "Reset";
            this.toolTipMain.SetToolTip(this.buttonResetSmgrPwd, "Sets the main Core Juris window title to Firm Name (helpful when using multiple c" +
        "ompanies)");
            this.buttonResetSmgrPwd.UseVisualStyleBackColor = true;
            this.buttonResetSmgrPwd.Click += new System.EventHandler(this.buttonResetSmgrPwd_Click);
            // 
            // buttonResetLastBackup
            // 
            this.buttonResetLastBackup.Location = new System.Drawing.Point(578, 268);
            this.buttonResetLastBackup.Name = "buttonResetLastBackup";
            this.buttonResetLastBackup.Size = new System.Drawing.Size(75, 23);
            this.buttonResetLastBackup.TabIndex = 20;
            this.buttonResetLastBackup.Text = "Reset";
            this.toolTipMain.SetToolTip(this.buttonResetLastBackup, "Resets last backup date to today\'s date");
            this.buttonResetLastBackup.UseVisualStyleBackColor = true;
            this.buttonResetLastBackup.Click += new System.EventHandler(this.buttonResetLastBackup_Click);
            // 
            // checkBoxJurisRpt
            // 
            this.checkBoxJurisRpt.AutoSize = true;
            this.checkBoxJurisRpt.Location = new System.Drawing.Point(183, 319);
            this.checkBoxJurisRpt.Name = "checkBoxJurisRpt";
            this.checkBoxJurisRpt.Size = new System.Drawing.Size(69, 17);
            this.checkBoxJurisRpt.TabIndex = 24;
            this.checkBoxJurisRpt.Text = "JurisRPT";
            this.toolTipMain.SetToolTip(this.checkBoxJurisRpt, "Default password is blank");
            this.checkBoxJurisRpt.UseVisualStyleBackColor = true;
            // 
            // checkBoxJurisRpt2
            // 
            this.checkBoxJurisRpt2.AutoSize = true;
            this.checkBoxJurisRpt2.Location = new System.Drawing.Point(256, 319);
            this.checkBoxJurisRpt2.Name = "checkBoxJurisRpt2";
            this.checkBoxJurisRpt2.Size = new System.Drawing.Size(75, 17);
            this.checkBoxJurisRpt2.TabIndex = 25;
            this.checkBoxJurisRpt2.Text = "JurisRPT2";
            this.toolTipMain.SetToolTip(this.checkBoxJurisRpt2, "Default password is \'JurisRPT2\'");
            this.checkBoxJurisRpt2.UseVisualStyleBackColor = true;
            // 
            // checkBoxJurisRo
            // 
            this.checkBoxJurisRo.AutoSize = true;
            this.checkBoxJurisRo.Location = new System.Drawing.Point(337, 319);
            this.checkBoxJurisRo.Name = "checkBoxJurisRo";
            this.checkBoxJurisRo.Size = new System.Drawing.Size(63, 17);
            this.checkBoxJurisRo.TabIndex = 26;
            this.checkBoxJurisRo.Text = "JurisRO";
            this.toolTipMain.SetToolTip(this.checkBoxJurisRo, "Default password is \'Juris58747\'");
            this.checkBoxJurisRo.UseVisualStyleBackColor = true;
            // 
            // buttonResetUsers
            // 
            this.buttonResetUsers.Location = new System.Drawing.Point(578, 314);
            this.buttonResetUsers.Name = "buttonResetUsers";
            this.buttonResetUsers.Size = new System.Drawing.Size(75, 23);
            this.buttonResetUsers.TabIndex = 29;
            this.buttonResetUsers.Text = "Reset";
            this.toolTipMain.SetToolTip(this.buttonResetUsers, "Select the user(s) to reset, then click reset");
            this.buttonResetUsers.UseVisualStyleBackColor = true;
            this.buttonResetUsers.Click += new System.EventHandler(this.buttonResetUsers_Click);
            // 
            // textBoxBackupPassword
            // 
            this.textBoxBackupPassword.Location = new System.Drawing.Point(428, 337);
            this.textBoxBackupPassword.Name = "textBoxBackupPassword";
            this.textBoxBackupPassword.Size = new System.Drawing.Size(143, 20);
            this.textBoxBackupPassword.TabIndex = 31;
            this.toolTipMain.SetToolTip(this.textBoxBackupPassword, "Assign a password to JurisBackup user (other users are not affected)");
            // 
            // textBoxUsersOnline
            // 
            this.textBoxUsersOnline.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxUsersOnline.Location = new System.Drawing.Point(258, 365);
            this.textBoxUsersOnline.Name = "textBoxUsersOnline";
            this.textBoxUsersOnline.ReadOnly = true;
            this.textBoxUsersOnline.Size = new System.Drawing.Size(47, 20);
            this.textBoxUsersOnline.TabIndex = 33;
            this.toolTipMain.SetToolTip(this.textBoxUsersOnline, "Current number of online flags set");
            // 
            // labelUsersOnline
            // 
            this.labelUsersOnline.BackColor = System.Drawing.SystemColors.Control;
            this.labelUsersOnline.Location = new System.Drawing.Point(120, 365);
            this.labelUsersOnline.Name = "labelUsersOnline";
            this.labelUsersOnline.Size = new System.Drawing.Size(132, 18);
            this.labelUsersOnline.TabIndex = 32;
            this.labelUsersOnline.Text = "Users Online:";
            this.toolTipMain.SetToolTip(this.labelUsersOnline, "Current number of online flags set");
            // 
            // buttonClearOnlineUsers
            // 
            this.buttonClearOnlineUsers.AutoSize = true;
            this.buttonClearOnlineUsers.Location = new System.Drawing.Point(311, 363);
            this.buttonClearOnlineUsers.Name = "buttonClearOnlineUsers";
            this.buttonClearOnlineUsers.Size = new System.Drawing.Size(116, 23);
            this.buttonClearOnlineUsers.TabIndex = 34;
            this.buttonClearOnlineUsers.Text = "Clear All Online Flags";
            this.toolTipMain.SetToolTip(this.buttonClearOnlineUsers, "Sets the main Core Juris window title to Firm Name (helpful when using multiple c" +
        "ompanies)");
            this.buttonClearOnlineUsers.UseVisualStyleBackColor = true;
            this.buttonClearOnlineUsers.Click += new System.EventHandler(this.buttonClearOnlineUsers_Click);
            // 
            // textBoxJurisVersion
            // 
            this.textBoxJurisVersion.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxJurisVersion.Location = new System.Drawing.Point(256, 165);
            this.textBoxJurisVersion.Name = "textBoxJurisVersion";
            this.textBoxJurisVersion.ReadOnly = true;
            this.textBoxJurisVersion.Size = new System.Drawing.Size(316, 20);
            this.textBoxJurisVersion.TabIndex = 10;
            // 
            // labelJurisVersion
            // 
            this.labelJurisVersion.BackColor = System.Drawing.SystemColors.Control;
            this.labelJurisVersion.Location = new System.Drawing.Point(118, 165);
            this.labelJurisVersion.Name = "labelJurisVersion";
            this.labelJurisVersion.Size = new System.Drawing.Size(132, 18);
            this.labelJurisVersion.TabIndex = 9;
            this.labelJurisVersion.Text = "Juris Version:";
            // 
            // textBoxJurisSuiteVersion
            // 
            this.textBoxJurisSuiteVersion.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxJurisSuiteVersion.Location = new System.Drawing.Point(256, 191);
            this.textBoxJurisSuiteVersion.Name = "textBoxJurisSuiteVersion";
            this.textBoxJurisSuiteVersion.ReadOnly = true;
            this.textBoxJurisSuiteVersion.Size = new System.Drawing.Size(316, 20);
            this.textBoxJurisSuiteVersion.TabIndex = 12;
            // 
            // labelJurisSuiteVersion
            // 
            this.labelJurisSuiteVersion.BackColor = System.Drawing.SystemColors.Control;
            this.labelJurisSuiteVersion.Location = new System.Drawing.Point(118, 191);
            this.labelJurisSuiteVersion.Name = "labelJurisSuiteVersion";
            this.labelJurisSuiteVersion.Size = new System.Drawing.Size(132, 18);
            this.labelJurisSuiteVersion.TabIndex = 11;
            this.labelJurisSuiteVersion.Text = "Juris Suite Version:";
            // 
            // textBoxDatabaseSize
            // 
            this.textBoxDatabaseSize.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxDatabaseSize.Location = new System.Drawing.Point(256, 217);
            this.textBoxDatabaseSize.Name = "textBoxDatabaseSize";
            this.textBoxDatabaseSize.ReadOnly = true;
            this.textBoxDatabaseSize.Size = new System.Drawing.Size(316, 20);
            this.textBoxDatabaseSize.TabIndex = 14;
            // 
            // labelDatabaseSize
            // 
            this.labelDatabaseSize.BackColor = System.Drawing.SystemColors.Control;
            this.labelDatabaseSize.Location = new System.Drawing.Point(118, 217);
            this.labelDatabaseSize.Name = "labelDatabaseSize";
            this.labelDatabaseSize.Size = new System.Drawing.Size(132, 18);
            this.labelDatabaseSize.TabIndex = 13;
            this.labelDatabaseSize.Text = "Database Size:";
            // 
            // textBoxSmgrPassword
            // 
            this.textBoxSmgrPassword.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxSmgrPassword.Location = new System.Drawing.Point(256, 243);
            this.textBoxSmgrPassword.Name = "textBoxSmgrPassword";
            this.textBoxSmgrPassword.ReadOnly = true;
            this.textBoxSmgrPassword.Size = new System.Drawing.Size(316, 20);
            this.textBoxSmgrPassword.TabIndex = 16;
            // 
            // labelSmgrPassword
            // 
            this.labelSmgrPassword.BackColor = System.Drawing.SystemColors.Control;
            this.labelSmgrPassword.Location = new System.Drawing.Point(118, 243);
            this.labelSmgrPassword.Name = "labelSmgrPassword";
            this.labelSmgrPassword.Size = new System.Drawing.Size(132, 18);
            this.labelSmgrPassword.TabIndex = 15;
            this.labelSmgrPassword.Text = "SMGR Password:";
            // 
            // textBoxLastBackup
            // 
            this.textBoxLastBackup.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxLastBackup.Location = new System.Drawing.Point(256, 270);
            this.textBoxLastBackup.Name = "textBoxLastBackup";
            this.textBoxLastBackup.ReadOnly = true;
            this.textBoxLastBackup.Size = new System.Drawing.Size(316, 20);
            this.textBoxLastBackup.TabIndex = 19;
            // 
            // labelLastBackup
            // 
            this.labelLastBackup.BackColor = System.Drawing.SystemColors.Control;
            this.labelLastBackup.Location = new System.Drawing.Point(118, 270);
            this.labelLastBackup.Name = "labelLastBackup";
            this.labelLastBackup.Size = new System.Drawing.Size(132, 18);
            this.labelLastBackup.TabIndex = 18;
            this.labelLastBackup.Text = "Last Backup:";
            // 
            // textBoxSqlVersion
            // 
            this.textBoxSqlVersion.BackColor = System.Drawing.SystemColors.Control;
            this.textBoxSqlVersion.Location = new System.Drawing.Point(256, 296);
            this.textBoxSqlVersion.Name = "textBoxSqlVersion";
            this.textBoxSqlVersion.ReadOnly = true;
            this.textBoxSqlVersion.Size = new System.Drawing.Size(316, 20);
            this.textBoxSqlVersion.TabIndex = 22;
            // 
            // labelSqlVersion
            // 
            this.labelSqlVersion.BackColor = System.Drawing.SystemColors.Control;
            this.labelSqlVersion.Location = new System.Drawing.Point(118, 296);
            this.labelSqlVersion.Name = "labelSqlVersion";
            this.labelSqlVersion.Size = new System.Drawing.Size(132, 18);
            this.labelSqlVersion.TabIndex = 21;
            this.labelSqlVersion.Text = "SQL Version:";
            // 
            // labelDbUsers
            // 
            this.labelDbUsers.BackColor = System.Drawing.SystemColors.Control;
            this.labelDbUsers.Location = new System.Drawing.Point(118, 319);
            this.labelDbUsers.Name = "labelDbUsers";
            this.labelDbUsers.Size = new System.Drawing.Size(58, 18);
            this.labelDbUsers.TabIndex = 23;
            this.labelDbUsers.Text = "DB Users:";
            // 
            // checkBoxAthensRo
            // 
            this.checkBoxAthensRo.AutoSize = true;
            this.checkBoxAthensRo.Location = new System.Drawing.Point(406, 318);
            this.checkBoxAthensRo.Name = "checkBoxAthensRo";
            this.checkBoxAthensRo.Size = new System.Drawing.Size(75, 17);
            this.checkBoxAthensRo.TabIndex = 27;
            this.checkBoxAthensRo.Text = "AthensRO";
            this.checkBoxAthensRo.UseVisualStyleBackColor = true;
            // 
            // checkBoxJurisBackup
            // 
            this.checkBoxJurisBackup.AutoSize = true;
            this.checkBoxJurisBackup.Location = new System.Drawing.Point(487, 318);
            this.checkBoxJurisBackup.Name = "checkBoxJurisBackup";
            this.checkBoxJurisBackup.Size = new System.Drawing.Size(84, 17);
            this.checkBoxJurisBackup.TabIndex = 28;
            this.checkBoxJurisBackup.Text = "JurisBackup";
            this.checkBoxJurisBackup.UseVisualStyleBackColor = true;
            // 
            // labelBackupPassword
            // 
            this.labelBackupPassword.BackColor = System.Drawing.SystemColors.Control;
            this.labelBackupPassword.Location = new System.Drawing.Point(297, 340);
            this.labelBackupPassword.Name = "labelBackupPassword";
            this.labelBackupPassword.Size = new System.Drawing.Size(124, 18);
            this.labelBackupPassword.TabIndex = 30;
            this.labelBackupPassword.Text = "JurisBackupPassword:";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.Window;
            this.BackgroundImageLayout = System.Windows.Forms.ImageLayout.Stretch;
            this.CancelButton = this.buttonExit;
            this.ClientSize = new System.Drawing.Size(658, 413);
            this.Controls.Add(this.buttonClearOnlineUsers);
            this.Controls.Add(this.textBoxUsersOnline);
            this.Controls.Add(this.labelUsersOnline);
            this.Controls.Add(this.textBoxBackupPassword);
            this.Controls.Add(this.labelBackupPassword);
            this.Controls.Add(this.buttonResetUsers);
            this.Controls.Add(this.checkBoxJurisBackup);
            this.Controls.Add(this.checkBoxAthensRo);
            this.Controls.Add(this.checkBoxJurisRo);
            this.Controls.Add(this.checkBoxJurisRpt2);
            this.Controls.Add(this.checkBoxJurisRpt);
            this.Controls.Add(this.labelDbUsers);
            this.Controls.Add(this.textBoxSqlVersion);
            this.Controls.Add(this.labelSqlVersion);
            this.Controls.Add(this.buttonResetLastBackup);
            this.Controls.Add(this.textBoxLastBackup);
            this.Controls.Add(this.labelLastBackup);
            this.Controls.Add(this.buttonResetSmgrPwd);
            this.Controls.Add(this.textBoxSmgrPassword);
            this.Controls.Add(this.labelSmgrPassword);
            this.Controls.Add(this.textBoxDatabaseSize);
            this.Controls.Add(this.labelDatabaseSize);
            this.Controls.Add(this.textBoxJurisSuiteVersion);
            this.Controls.Add(this.labelJurisSuiteVersion);
            this.Controls.Add(this.textBoxJurisVersion);
            this.Controls.Add(this.labelJurisVersion);
            this.Controls.Add(this.buttonFirmName);
            this.Controls.Add(this.textBoxFirmName);
            this.Controls.Add(this.labelFirmName);
            this.Controls.Add(this.buttonExit);
            this.Controls.Add(this.statusGroupBox);
            this.Controls.Add(this.labelDescription);
            this.Controls.Add(this.listBoxCompanies);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.LexisNexisLogoPictureBox);
            this.Controls.Add(this.JurisLogoImageBox);
            this.ForeColor = System.Drawing.SystemColors.WindowText;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Juris DBInfo v2.71";
            this.Load += new System.EventHandler(this.Form1_Load);
            ((System.ComponentModel.ISupportInitialize)(this.JurisLogoImageBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.LexisNexisLogoPictureBox)).EndInit();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.statusGroupBox.ResumeLayout(false);
            this.statusGroupBox.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.PictureBox JurisLogoImageBox;
        private System.Windows.Forms.PictureBox LexisNexisLogoPictureBox;
        private System.Windows.Forms.StatusStrip statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel;
        private System.Windows.Forms.ListBox listBoxCompanies;
        private System.Windows.Forms.Label labelDescription;
        private System.Windows.Forms.GroupBox statusGroupBox;
        private System.Windows.Forms.Label labelCurrentStatus;
        private System.Windows.Forms.ProgressBar progressBar;
        private System.Windows.Forms.Label labelPercentComplete;
        private System.Windows.Forms.Button buttonExit;
        private System.Windows.Forms.Label labelFirmName;
        private System.Windows.Forms.TextBox textBoxFirmName;
        private System.Windows.Forms.Button buttonFirmName;
        private System.Windows.Forms.ToolTip toolTipMain;
        private System.Windows.Forms.TextBox textBoxJurisVersion;
        private System.Windows.Forms.Label labelJurisVersion;
        private System.Windows.Forms.TextBox textBoxJurisSuiteVersion;
        private System.Windows.Forms.Label labelJurisSuiteVersion;
        private System.Windows.Forms.TextBox textBoxDatabaseSize;
        private System.Windows.Forms.Label labelDatabaseSize;
        private System.Windows.Forms.TextBox textBoxSmgrPassword;
        private System.Windows.Forms.Label labelSmgrPassword;
        private System.Windows.Forms.Button buttonResetSmgrPwd;
        private System.Windows.Forms.TextBox textBoxLastBackup;
        private System.Windows.Forms.Label labelLastBackup;
        private System.Windows.Forms.Button buttonResetLastBackup;
        private System.Windows.Forms.TextBox textBoxSqlVersion;
        private System.Windows.Forms.Label labelSqlVersion;
        private System.Windows.Forms.Label labelDbUsers;
        private System.Windows.Forms.CheckBox checkBoxJurisRpt;
        private System.Windows.Forms.CheckBox checkBoxJurisRpt2;
        private System.Windows.Forms.CheckBox checkBoxJurisRo;
        private System.Windows.Forms.CheckBox checkBoxAthensRo;
        private System.Windows.Forms.CheckBox checkBoxJurisBackup;
        private System.Windows.Forms.Button buttonResetUsers;
        private System.Windows.Forms.Label labelBackupPassword;
        private System.Windows.Forms.TextBox textBoxBackupPassword;
        private System.Windows.Forms.TextBox textBoxUsersOnline;
        private System.Windows.Forms.Label labelUsersOnline;
        private System.Windows.Forms.Button buttonClearOnlineUsers;
    }
}

