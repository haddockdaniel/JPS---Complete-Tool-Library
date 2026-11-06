
namespace JUtility
{
	partial class FUtil
	{

		#region "Upgrade Support "
		private static FUtil m_vb6FormDefInstance;
		private static bool m_InitializingDefInstance;
		public static FUtil DefInstance
		{
			get
			{
				if (m_vb6FormDefInstance == null || m_vb6FormDefInstance.IsDisposed)
				{
					m_InitializingDefInstance = true;
					m_vb6FormDefInstance = CreateInstance();
					m_InitializingDefInstance = false;
				}
				return m_vb6FormDefInstance;
			}
			set
			{
				m_vb6FormDefInstance = value;
			}
		}

		#endregion
		#region "Windows Form Designer generated code "
		public static FUtil CreateInstance()
		{
			FUtil theInstance = new FUtil();
			return theInstance;
		}
		private string[] visualControls = new string[]{"components", "ToolTipMain", "txtBatchTitle", "cmdImportVouchers", "OpenFileDialogOpen", "optImportDuplicates", "optRejectDuplicates", "Frame2", "txtPercentage", "txtStatus", "ProgressBar1", "Label4", "Label3", "Frame1", "_statBar_Panel1", "statBar", "lstCompanies", "Label1", "btnExit", "Image1", "lblDescription", "_imgSideBar_0", "imgSideBar", "listBoxHelper1"};
		//Required by the Windows Form Designer
		private System.ComponentModel.IContainer components;
		public System.Windows.Forms.ToolTip ToolTipMain;
		public System.Windows.Forms.TextBox txtBatchTitle;
		public System.Windows.Forms.Button cmdImportVouchers;
		public System.Windows.Forms.OpenFileDialog OpenFileDialogOpen;
		public System.Windows.Forms.RadioButton optImportDuplicates;
		public System.Windows.Forms.RadioButton optRejectDuplicates;
		public System.Windows.Forms.GroupBox Frame2;
		public System.Windows.Forms.TextBox txtPercentage;
		public System.Windows.Forms.TextBox txtStatus;
		public System.Windows.Forms.ProgressBar ProgressBar1;
		public System.Windows.Forms.Label Label4;
		public System.Windows.Forms.Label Label3;
		public System.Windows.Forms.GroupBox Frame1;
		private System.Windows.Forms.ToolStripStatusLabel _statBar_Panel1;
		public System.Windows.Forms.StatusStrip statBar;
		public System.Windows.Forms.ListBox lstCompanies;
		public System.Windows.Forms.Label Label1;
		public System.Windows.Forms.PictureBox btnExit;
		public System.Windows.Forms.PictureBox Image1;
		public System.Windows.Forms.Label lblDescription;
		private System.Windows.Forms.PictureBox _imgSideBar_0;
		public System.Windows.Forms.PictureBox[] imgSideBar = new System.Windows.Forms.PictureBox[1];
		private UpgradeHelpers.Gui.ListBoxHelper listBoxHelper1;
		//NOTE: The following procedure is required by the Windows Form Designer
		//It can be modified using the Windows Form Designer.
		//Do not modify it using the code editor.
		[System.Diagnostics.DebuggerStepThrough()]
		private void InitializeComponent()
		{
			this.components = new System.ComponentModel.Container();
			System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FUtil));
			this.ToolTipMain = new System.Windows.Forms.ToolTip(this.components);
			this.txtBatchTitle = new System.Windows.Forms.TextBox();
			this.cmdImportVouchers = new System.Windows.Forms.Button();
			this.OpenFileDialogOpen = new System.Windows.Forms.OpenFileDialog();
			this.Frame2 = new System.Windows.Forms.GroupBox();
			this.optImportDuplicates = new System.Windows.Forms.RadioButton();
			this.optRejectDuplicates = new System.Windows.Forms.RadioButton();
			this.Frame1 = new System.Windows.Forms.GroupBox();
			this.txtPercentage = new System.Windows.Forms.TextBox();
			this.txtStatus = new System.Windows.Forms.TextBox();
			this.ProgressBar1 = new System.Windows.Forms.ProgressBar();
			this.Label4 = new System.Windows.Forms.Label();
			this.Label3 = new System.Windows.Forms.Label();
			this.statBar = new System.Windows.Forms.StatusStrip();
			this._statBar_Panel1 = new System.Windows.Forms.ToolStripStatusLabel();
			this.lstCompanies = new System.Windows.Forms.ListBox();
			this.Label1 = new System.Windows.Forms.Label();
			this.btnExit = new System.Windows.Forms.PictureBox();
			this.Image1 = new System.Windows.Forms.PictureBox();
			this.lblDescription = new System.Windows.Forms.Label();
			this._imgSideBar_0 = new System.Windows.Forms.PictureBox();
			this.Frame2.SuspendLayout();
			this.Frame1.SuspendLayout();
			this.statBar.SuspendLayout();
			this.SuspendLayout();
			this.listBoxHelper1 = new UpgradeHelpers.Gui.ListBoxHelper(this.components);
			// 
			// txtBatchTitle
			// 
			this.txtBatchTitle.AcceptsReturn = true;
			this.txtBatchTitle.BackColor = System.Drawing.SystemColors.Window;
			this.txtBatchTitle.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
			this.txtBatchTitle.Cursor = System.Windows.Forms.Cursors.IBeam;
			this.txtBatchTitle.ForeColor = System.Drawing.SystemColors.WindowText;
			this.txtBatchTitle.Location = new System.Drawing.Point(112, 144);
			this.txtBatchTitle.MaxLength = 0;
			this.txtBatchTitle.Name = "txtBatchTitle";
			this.txtBatchTitle.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.txtBatchTitle.Size = new System.Drawing.Size(449, 19);
			this.txtBatchTitle.TabIndex = 13;
			this.txtBatchTitle.Text = "Voucher Import Batch #";
			// 
			// cmdImportVouchers
			// 
			this.cmdImportVouchers.BackColor = System.Drawing.SystemColors.Control;
			this.cmdImportVouchers.Cursor = System.Windows.Forms.Cursors.Default;
			this.cmdImportVouchers.ForeColor = System.Drawing.SystemColors.ControlText;
			this.cmdImportVouchers.Location = new System.Drawing.Point(112, 200);
			this.cmdImportVouchers.Name = "cmdImportVouchers";
			this.cmdImportVouchers.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.cmdImportVouchers.Size = new System.Drawing.Size(441, 33);
			this.cmdImportVouchers.TabIndex = 12;
			this.cmdImportVouchers.Text = "Import Voucher File";
			this.cmdImportVouchers.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
			this.cmdImportVouchers.UseVisualStyleBackColor = false;
			this.cmdImportVouchers.Click += new System.EventHandler(this.cmdImportVouchers_Click);
			// 
			// Frame2
			// 
			this.Frame2.BackColor = System.Drawing.SystemColors.Window;
			this.Frame2.Controls.Add(this.optImportDuplicates);
			this.Frame2.Controls.Add(this.optRejectDuplicates);
			this.Frame2.Enabled = true;
			this.Frame2.ForeColor = System.Drawing.SystemColors.ControlText;
			this.Frame2.Location = new System.Drawing.Point(104, 256);
			this.Frame2.Name = "Frame2";
			this.Frame2.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.Frame2.Size = new System.Drawing.Size(465, 57);
			this.Frame2.TabIndex = 9;
			this.Frame2.Text = "Duplicate Invoices";
			this.Frame2.Visible = true;
			// 
			// optImportDuplicates
			// 
			this.optImportDuplicates.Appearance = System.Windows.Forms.Appearance.Normal;
			this.optImportDuplicates.BackColor = System.Drawing.SystemColors.Window;
			this.optImportDuplicates.CausesValidation = true;
			this.optImportDuplicates.CheckAlign = System.Drawing.ContentAlignment.MiddleLeft;
			this.optImportDuplicates.Checked = false;
			this.optImportDuplicates.Cursor = System.Windows.Forms.Cursors.Default;
			this.optImportDuplicates.Enabled = true;
			this.optImportDuplicates.ForeColor = System.Drawing.SystemColors.ControlText;
			this.optImportDuplicates.Location = new System.Drawing.Point(8, 32);
			this.optImportDuplicates.Name = "optImportDuplicates";
			this.optImportDuplicates.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.optImportDuplicates.Size = new System.Drawing.Size(449, 17);
			this.optImportDuplicates.TabIndex = 11;
			this.optImportDuplicates.TabStop = true;
			this.optImportDuplicates.Text = "Increment the Invoice Nbr and import anyway (\"_#\" is added)";
			this.optImportDuplicates.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
			this.optImportDuplicates.Visible = true;
			// 
			// optRejectDuplicates
			// 
			this.optRejectDuplicates.Appearance = System.Windows.Forms.Appearance.Normal;
			this.optRejectDuplicates.BackColor = System.Drawing.SystemColors.Window;
			this.optRejectDuplicates.CausesValidation = true;
			this.optRejectDuplicates.CheckAlign = System.Drawing.ContentAlignment.MiddleLeft;
			this.optRejectDuplicates.Checked = true;
			this.optRejectDuplicates.Cursor = System.Windows.Forms.Cursors.Default;
			this.optRejectDuplicates.Enabled = true;
			this.optRejectDuplicates.ForeColor = System.Drawing.SystemColors.ControlText;
			this.optRejectDuplicates.Location = new System.Drawing.Point(8, 16);
			this.optRejectDuplicates.Name = "optRejectDuplicates";
			this.optRejectDuplicates.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.optRejectDuplicates.Size = new System.Drawing.Size(449, 17);
			this.optRejectDuplicates.TabIndex = 10;
			this.optRejectDuplicates.TabStop = true;
			this.optRejectDuplicates.Text = "Reject all duplicate Vendor Invoices (same Vendor, Invoice Nbr and Date)";
			this.optRejectDuplicates.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
			this.optRejectDuplicates.Visible = true;
			// 
			// Frame1
			// 
			this.Frame1.BackColor = System.Drawing.SystemColors.Window;
			this.Frame1.Controls.Add(this.txtPercentage);
			this.Frame1.Controls.Add(this.txtStatus);
			this.Frame1.Controls.Add(this.ProgressBar1);
			this.Frame1.Controls.Add(this.Label4);
			this.Frame1.Controls.Add(this.Label3);
			this.Frame1.Enabled = true;
			this.Frame1.ForeColor = System.Drawing.SystemColors.ControlText;
			this.Frame1.Location = new System.Drawing.Point(104, 56);
			this.Frame1.Name = "Frame1";
			this.Frame1.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.Frame1.Size = new System.Drawing.Size(465, 65);
			this.Frame1.TabIndex = 3;
			this.Frame1.Text = "Utility Status:";
			this.Frame1.Visible = true;
			// 
			// txtPercentage
			// 
			this.txtPercentage.AcceptsReturn = true;
			this.txtPercentage.BackColor = System.Drawing.SystemColors.Window;
			this.txtPercentage.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.txtPercentage.Cursor = System.Windows.Forms.Cursors.IBeam;
			this.txtPercentage.Enabled = false;
			this.txtPercentage.ForeColor = System.Drawing.SystemColors.WindowText;
			this.txtPercentage.Location = new System.Drawing.Point(360, 8);
			this.txtPercentage.MaxLength = 0;
			this.txtPercentage.Name = "txtPercentage";
			this.txtPercentage.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.txtPercentage.Size = new System.Drawing.Size(25, 11);
			this.txtPercentage.TabIndex = 8;
			this.txtPercentage.TabStop = false;
			// 
			// txtStatus
			// 
			this.txtStatus.AcceptsReturn = true;
			this.txtStatus.BackColor = System.Drawing.SystemColors.Window;
			this.txtStatus.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.txtStatus.Cursor = System.Windows.Forms.Cursors.IBeam;
			this.txtStatus.Enabled = false;
			this.txtStatus.ForeColor = System.Drawing.SystemColors.WindowText;
			this.txtStatus.Location = new System.Drawing.Point(96, 40);
			this.txtStatus.MaxLength = 0;
			this.txtStatus.Name = "txtStatus";
			this.txtStatus.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.txtStatus.Size = new System.Drawing.Size(361, 19);
			this.txtStatus.TabIndex = 7;
			this.txtStatus.TabStop = false;
			// 
			// ProgressBar1
			// 
			this.ProgressBar1.Location = new System.Drawing.Point(8, 24);
			this.ProgressBar1.Name = "ProgressBar1";
			this.ProgressBar1.Size = new System.Drawing.Size(449, 17);
			this.ProgressBar1.TabIndex = 4;
			// 
			// Label4
			// 
			this.Label4.BackColor = System.Drawing.SystemColors.Window;
			this.Label4.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.Label4.Cursor = System.Windows.Forms.Cursors.Default;
			this.Label4.ForeColor = System.Drawing.SystemColors.ControlText;
			this.Label4.Location = new System.Drawing.Point(8, 40);
			this.Label4.Name = "Label4";
			this.Label4.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.Label4.Size = new System.Drawing.Size(89, 17);
			this.Label4.TabIndex = 6;
			this.Label4.Text = "Current Status:";
			// 
			// Label3
			// 
			this.Label3.BackColor = System.Drawing.SystemColors.Window;
			this.Label3.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.Label3.Cursor = System.Windows.Forms.Cursors.Default;
			this.Label3.ForeColor = System.Drawing.SystemColors.ControlText;
			this.Label3.Location = new System.Drawing.Point(384, 8);
			this.Label3.Name = "Label3";
			this.Label3.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.Label3.Size = new System.Drawing.Size(73, 17);
			this.Label3.TabIndex = 5;
			this.Label3.Text = "% Complete";
			// 
			// statBar
			// 
			this.statBar.BackColor = System.Drawing.SystemColors.Control;
			this.statBar.Dock = System.Windows.Forms.DockStyle.Bottom;
			this.statBar.Location = new System.Drawing.Point(0, 366);
			this.statBar.Name = "statBar";
			this.statBar.ShowItemToolTips = true;
			this.statBar.Size = new System.Drawing.Size(570, 25);
			this.statBar.TabIndex = 1;
			this.statBar.Text = "Status: Ready to Execute";
			this.statBar.Items.AddRange(new System.Windows.Forms.ToolStripItem[]{this._statBar_Panel1});
			// 
			// _statBar_Panel1
			// 
			this._statBar_Panel1.AutoSize = false;
			this._statBar_Panel1.BorderSides = (System.Windows.Forms.ToolStripStatusLabelBorderSides) (System.Windows.Forms.ToolStripStatusLabelBorderSides.Left | System.Windows.Forms.ToolStripStatusLabelBorderSides.Top | System.Windows.Forms.ToolStripStatusLabelBorderSides.Right | System.Windows.Forms.ToolStripStatusLabelBorderSides.Bottom);
			this._statBar_Panel1.BorderStyle = System.Windows.Forms.Border3DStyle.SunkenOuter;
			this._statBar_Panel1.DoubleClickEnabled = true;
			this._statBar_Panel1.Margin = new System.Windows.Forms.Padding(0);
			this._statBar_Panel1.Name = "";
			this._statBar_Panel1.Size = new System.Drawing.Size(96, 25);
			this._statBar_Panel1.Tag = "";
			this._statBar_Panel1.Text = "Status:";
			this._statBar_Panel1.Text = "Status:";
			this._statBar_Panel1.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
			this._statBar_Panel1.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
			// 
			// lstCompanies
			// 
			this.lstCompanies.BackColor = System.Drawing.SystemColors.Window;
			this.lstCompanies.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
			this.lstCompanies.CausesValidation = true;
			this.lstCompanies.Cursor = System.Windows.Forms.Cursors.Default;
			this.lstCompanies.Enabled = true;
			this.lstCompanies.ForeColor = System.Drawing.Color.FromArgb(0, 0, 64);
			this.lstCompanies.IntegralHeight = true;
			this.lstCompanies.Location = new System.Drawing.Point(104, 0);
			this.lstCompanies.MultiColumn = false;
			this.lstCompanies.Name = "lstCompanies";
			this.lstCompanies.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.lstCompanies.Size = new System.Drawing.Size(201, 59);
			this.lstCompanies.Sorted = false;
			this.lstCompanies.TabIndex = 0;
			this.lstCompanies.TabStop = false;
			this.lstCompanies.Visible = true;
			this.lstCompanies.SelectedIndexChanged += new System.EventHandler(this.lstCompanies_SelectedIndexChanged);
			// 
			// Label1
			// 
			this.Label1.BackColor = System.Drawing.SystemColors.Window;
			this.Label1.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.Label1.Cursor = System.Windows.Forms.Cursors.Default;
			this.Label1.ForeColor = System.Drawing.SystemColors.ControlText;
			this.Label1.Location = new System.Drawing.Point(112, 128);
			this.Label1.Name = "Label1";
			this.Label1.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.Label1.Size = new System.Drawing.Size(169, 17);
			this.Label1.TabIndex = 14;
			this.Label1.Text = "Batch Title:";
			// 
			// btnExit
			// 
			this.btnExit.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.btnExit.Cursor = System.Windows.Forms.Cursors.Default;
			this.btnExit.Enabled = true;
			this.btnExit.Image = (System.Drawing.Image) resources.GetObject("btnExit.Image");
			this.btnExit.Location = new System.Drawing.Point(512, 320);
			this.btnExit.Name = "btnExit";
			this.btnExit.Size = new System.Drawing.Size(52, 39);
			this.btnExit.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Normal;
			this.btnExit.Visible = true;
			this.btnExit.Click += new System.EventHandler(this.btnExit_Click);
			// 
			// Image1
			// 
			this.Image1.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this.Image1.Cursor = System.Windows.Forms.Cursors.Default;
			this.Image1.Enabled = true;
			this.Image1.Image = (System.Drawing.Image) resources.GetObject("Image1.Image");
			this.Image1.Location = new System.Drawing.Point(8, 336);
			this.Image1.Name = "Image1";
			this.Image1.Size = new System.Drawing.Size(96, 28);
			this.Image1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Normal;
			this.Image1.Visible = true;
			// 
			// lblDescription
			// 
			this.lblDescription.BackColor = System.Drawing.SystemColors.Window;
			this.lblDescription.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
			this.lblDescription.Cursor = System.Windows.Forms.Cursors.Default;
			this.lblDescription.Enabled = false;
			this.lblDescription.ForeColor = System.Drawing.SystemColors.ControlText;
			this.lblDescription.Location = new System.Drawing.Point(312, 0);
			this.lblDescription.Name = "lblDescription";
			this.lblDescription.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.lblDescription.Size = new System.Drawing.Size(257, 57);
			this.lblDescription.TabIndex = 2;
			this.lblDescription.Text = "This utility will import a CSV file with invoice, G/L and Expense information into a new, unposted payment voucher batch.  See user guide for more information.";
			// 
			// _imgSideBar_0
			// 
			this._imgSideBar_0.BorderStyle = System.Windows.Forms.BorderStyle.None;
			this._imgSideBar_0.Cursor = System.Windows.Forms.Cursors.Default;
			this._imgSideBar_0.Enabled = true;
			this._imgSideBar_0.Image = (System.Drawing.Image) resources.GetObject("_imgSideBar_0.Image");
			this._imgSideBar_0.Location = new System.Drawing.Point(0, 0);
			this._imgSideBar_0.Name = "_imgSideBar_0";
			this._imgSideBar_0.Size = new System.Drawing.Size(104, 336);
			this._imgSideBar_0.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Normal;
			this._imgSideBar_0.Visible = true;
			// 
			// FUtil
			// 
			this.AutoScaleDimensions = new System.Drawing.SizeF(7, 13);
			this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			this.BackColor = System.Drawing.SystemColors.Window;
			this.ClientSize = new System.Drawing.Size(570, 391);
			this.Controls.Add(this.txtBatchTitle);
			this.Controls.Add(this.cmdImportVouchers);
			this.Controls.Add(this.Frame2);
			this.Controls.Add(this.Frame1);
			this.Controls.Add(this.statBar);
			this.Controls.Add(this.lstCompanies);
			this.Controls.Add(this.Label1);
			this.Controls.Add(this.btnExit);
			this.Controls.Add(this.Image1);
			this.Controls.Add(this.lblDescription);
			this.Controls.Add(this._imgSideBar_0);
			this.Cursor = System.Windows.Forms.Cursors.Default;
			this.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25f, System.Drawing.FontStyle.Bold | System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, 0);
			this.ForeColor = System.Drawing.SystemColors.WindowText;
			this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
			this.Icon = (System.Drawing.Icon) resources.GetObject("FUtil.Icon");
			this.Location = new System.Drawing.Point(210, 163);
			this.MaximizeBox = false;
			this.MinimizeBox = false;
			this.Name = "FUtil";
			this.RightToLeft = System.Windows.Forms.RightToLeft.No;
			this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
			this.Text = "VoucherImport v2.1.5";
			listBoxHelper1.SetSelectionMode(this.lstCompanies, System.Windows.Forms.SelectionMode.One);
			this.Closed += new System.EventHandler(this.FUtil_Closed);
			this.Frame2.ResumeLayout(false);
			this.Frame1.ResumeLayout(false);
			this.statBar.ResumeLayout(false);
			this.ResumeLayout(false);
		}
		void ReLoadForm(bool addEvents)
		{
			InitializeimgSideBar();
		}
		void InitializeimgSideBar()
		{
			this.imgSideBar = new System.Windows.Forms.PictureBox[1];
			this.imgSideBar[0] = _imgSideBar_0;
		}
		#endregion
	}
}