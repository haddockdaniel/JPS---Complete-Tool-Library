using Microsoft.VisualBasic;
using System;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using UpgradeHelpers.DB.ADO;
using UpgradeHelpers.Gui;
using UpgradeHelpers.Helpers;

namespace JUtility
{
	internal partial class FUtil
		: System.Windows.Forms.Form
	{

		//UPGRADE_NOTE: (2041) The following line was commented. More Information: http://www.vbtonet.com/ewis/ewi2041.aspx
		//[DllImport("kernel32.dll", EntryPoint = "GetComputerNameA", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
		//extern public static int GetComputerName([MarshalAs(UnmanagedType.VBByRefStr)] ref string lpBuffer, ref int nSize);
		//UPGRADE_NOTE: (2041) The following line was commented. More Information: http://www.vbtonet.com/ewis/ewi2041.aspx
		//[DllImport("mpr.dll", EntryPoint = "WNetGetUserA", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
		//extern public static int WNetGetUser(int lpName, [MarshalAs(UnmanagedType.VBByRefStr)] ref string lpUserName, ref int lpnLength);
		public string strCompanyCode = "";
		public string JurisDBName = "";
		public string JBillsDBName = "";
		public int fldCli = 0;
		public int fldMat = 0;

		public FUtil()
			: base()
		{
			if (m_vb6FormDefInstance == null)
			{
				if (m_InitializingDefInstance)
				{
					m_vb6FormDefInstance = this;
				}
				else
				{
					try
					{
						//For the start-up form, the first instance created is the default instance.
						if (System.Reflection.Assembly.GetExecutingAssembly().EntryPoint != null && System.Reflection.Assembly.GetExecutingAssembly().EntryPoint.DeclaringType == this.GetType())
						{
							m_vb6FormDefInstance = this;
						}
					}
					catch
					{
					}
				}
			}
			//This call is required by the Windows Form Designer.
			InitializeComponent();
			ReLoadForm(false);
		}


		//===================================================================================
		//Version information
		//v1.0.1     - Corrected handling of Vendor Codes to handle numeric as well as character
		//           - Now archiving the last five log files, still saved in the application folder
		//v1.0.2     - Corrected bug in log file archiving
		//           - Corrected checking of expense amounts - value should be optional.  Handles nulls.
		//v1.0.3     - Added error handling to most procedures
		//           - Voucher Date now set to invoice date, not current date
		//           - Puts new batch in current accounting period instead of today's date
		//v1.1       - Adds two new columns to the import file specification.  The new fields allow the user to specify custom Due Date and
		//           - Discount Date.
		//v1.1.1     - Change default batch name to "FAC Batch #[batch nbr]"
		//           - Make the voucher expense entries show bill note and narrative info as set in Firm Options, if not set in the import file.
		//v1.1.2     - Now creates a "schema.ini" file in the import file folder to define column types explicitly
		//           - Also, now handles negative invoice amounts
		//v2.0       - Now accepts trust vouchers.  Import specs have changed to suit.  Voucher rows are now in type 'A' (for
		//             A/P vouchers) or type 'T' (for trust vouchers).  Trust vouchers do not specify matter or G/L distributions.
		//v2.1       - Expense distributions changed to allow the user to specify expense task code.  Column added to import file.
		//v2.1.1     - Columns added to specify TrustBank for trust vouchers.
		//v2.1.2     - Columns added to specify SeparateCheck and AP Account for AP Vouchers
		//v2.1.4     - Fixed bug in handling Bill Note for expenses.
		//v2.1.5     - Fixed bug in which Invoice Number exceeded 20 characters when _copy was appended.  Now truncated to 20 characters.

		private bool VerifyFirmName()
		{
			//    Dim SQL     As String
			//    Dim rsDB    As ADODB.Recordset
			//
			//    SQL = "SELECT CASE WHEN SpTxtValue LIKE '%firm name%' THEN 'Y' ELSE 'N' END AS Firm FROM SysParam WHERE SpName = 'FirmName'"
			//    Cmd.CommandText = SQL
			//    Set rsDB = Cmd.Execute
			//
			//    If rsDB!Firm = "Y" Then
			return true;
			//    Else
			//        VerifyFirmName = False
			//    End If

		}

		private void btnExit_Click(Object eventSender, EventArgs eventArgs)
		{
			//This sub is called when the Exit button is clicked.
			//If the database is open, it closes it, so you do not have to explicitly close the database
			if (mJUTGlobal.DBOpen)
			{
				mJUTGlobal.CloseDB();
			}
			mJUTGlobal.UnloadAllForms(this.Name);
			this.Close();
		}

		public void LoadCompanies()
		{
			int lngIndex = 0;
			foreach (JurisAuthenticator.Instance objInstance2 in mInstance.objInstances)
			{
				mInstance.objInstance = objInstance2;
				if (mInstance.objInstance.IsValid == JurisAuthenticator.JurisErrorCodes.jecSuccess)
				{
					lstCompanies.AddItem(mInstance.objInstance.Name, lngIndex);
					lstCompanies.SetItemData(lngIndex, Convert.ToInt32(Double.Parse(mInstance.objInstance.Code)));
					if (mInstance.objInstance.Default != JurisAuthenticator.JurisDefaultCompany.jdcNone)
					{ //If a company is checked as 'Default,' select that company
						ListBoxHelper.SetSelected(lstCompanies, lngIndex, true); //Calls lst_Companies_Click
					}
					lngIndex++;
				}
				mInstance.objInstance = null;
			}

			if (ListBoxHelper.GetSelectedIndex(lstCompanies) == -1)
			{
				ListBoxHelper.SetSelected(lstCompanies, 0, true);
			} //If no companies are default, select the first one
		}


		private void cmdImportVouchers_Click(Object eventSender, EventArgs eventArgs)
		{
			if (!VerifyFirmName())
			{
				MessageBox.Show("This build of APVoucherImport is licensed for [firm name] only.", Application.ProductName);
				return;
			}
			OpenFileDialogOpen.Title = "Select Voucher Import File";
			OpenFileDialogOpen.FileName = "";
			//UPGRADE_WARNING: (2081) Filter has a new behavior. More Information: http://www.vbtonet.com/ewis/ewi2081.aspx
			OpenFileDialogOpen.Filter = "*.csv|*.*";
			OpenFileDialogOpen.FilterIndex = 1;
			OpenFileDialogOpen.ShowDialog();

			string tempRefParam = (OpenFileDialogOpen.FileName);
			ReadVoucherCSV(ref tempRefParam);
		}

		private void lstCompanies_SelectedIndexChanged(Object eventSender, EventArgs eventArgs)
		{
			//This sub is called when a company is selected.  It is called at utility startup by LoadCompanies
			if (mJUTGlobal.DBOpen)
			{
				mJUTGlobal.CloseDB();
			}
			strCompanyCode = "Company" + lstCompanies.GetItemData(ListBoxHelper.GetSelectedIndex(lstCompanies)).ToString();
			object tempRefParam = strCompanyCode;
			mInstance.objInstance = mInstance.objInstances.get_Item(ref tempRefParam);
			strCompanyCode = ReflectionHelper.GetPrimitiveValue<string>(tempRefParam);
			//JurisDBName and JBillsDBName are global variables that store the database names for Juris and JBills
			JurisDBName = mInstance.objInstance.DatabaseName;
			JBillsDBName = "JBills" + mInstance.objInstance.Code;
			mJUTGlobal.OpenDB();
			if (mJUTGlobal.DBOpen)
			{
				GetFieldLengths();
			}
		}

		private void ReadVoucherCSV(ref string FILENAME)
		{
			//Reads Voucher Import CSV into an ADODB.RecordSet
			//Calls RecordSetIsValid to make sure everything looks okay
			DbConnection cnCSV = null;
			ADORecordSetHelper rsCSV = null;
			string ConnectionString = "";
			int i = 0;
			string SQL = "";
			int RecCount = 0;
			string dirPath = "";

			//UPGRADE_TODO: (1065) Error handling statement (On Error Goto) could not be converted. More Information: http://www.vbtonet.com/ewis/ewi1065.aspx
			UpgradeHelpers.Helpers.NotUpgradedHelper.NotifyNotUpgradedElement("On Error Goto Label (Error)");

			DeleteLog();
			i = 0;

			while(i < Strings.Len(FILENAME))
			{
				if (FILENAME.Substring(Strings.Len(FILENAME) - i - 1, Math.Min(1, FILENAME.Length - (Strings.Len(FILENAME) - i - 1))) == "\\")
				{
					dirPath = FILENAME.Substring(0, Math.Min(Strings.Len(FILENAME) - i, FILENAME.Length));
					FILENAME = FILENAME.Substring(Strings.Len(FILENAME) - i);
					break;
				}
				i++;
			};
			//txtImportPath.Text = dirPath
			//txtImportFileName.Text = FILENAME
			CreateDelimSchema(dirPath, FILENAME);
			cnCSV = UpgradeHelpers.DB.AdoFactoryManager.GetFactory().CreateConnection();
			ConnectionString = "Driver={Microsoft Text Driver (*.txt; *.csv)};Dbq=" + dirPath + ";" + 
			                   "Extensions=asc,csv,tab,txt";
			//UPGRADE_TODO: (7010) The connection string must be verified to fullfill the .NET data provider connection string requirements. More Information: http://www.vbtonet.com/ewis/ewi7010.aspx
			cnCSV.ConnectionString = ConnectionString;
			cnCSV.Open();
			rsCSV = new ADORecordSetHelper("");

			rsCSV.CursorLocation = CursorLocationEnum.adUseClient;
			rsCSV.Open("SELECT * FROM [" + FILENAME + "]", cnCSV, UpgradeHelpers.DB.LockTypeEnum.LockReadOnly);


			RecCount = 0;

			while(!rsCSV.EOF)
			{
				RecCount++;
				rsCSV.MoveNext();
			};
			rsCSV.MoveFirst();
			if (RecordSetIsValid(rsCSV))
			{
				UpdateStatus("Ready to import", 1, 1);
				if (MessageBox.Show("The import file meets file format specifications.  Do you wish to import it now, creating new a new, unposted Voucher Batch?", Application.ProductName, MessageBoxButtons.YesNo) == System.Windows.Forms.DialogResult.Yes)
				{
					ImportVouchers(rsCSV);
				}
				else
				{
					MessageBox.Show("Operation cancelled.  File has not been imported.", Application.ProductName);
					UpdateStatus("Operation cancelled.", 0, 1);
					goto ExitSub;
				}
			}
			else
			{
				MessageBox.Show("The file you tried to import has formatting issues, and could not be imported.  See VoucherImportLog.txt for details.", Application.ProductName);
				UpdateStatus("Import error", 0, 1);
				goto ExitSub;
			}
			UpdateStatus("Ready", 1, 1);

ExitSub:
			Cursor = Cursors.Default;
			UpdateStatus("Ready", 1, 1);
			return;
Error:
			MessageBox.Show("Error in procedure ReadVoucherCSV:" + Environment.NewLine + Information.Err().Description, Application.ProductName);
			LogFile(SQL);
			goto ExitSub;

		}

		private void ImportVouchers(ADORecordSetHelper rsVch)
		{
			string SQL = "";
			ADORecordSetHelper rsSys = null;
			int lastVchBatch = 0;
			int RecNbr = 0;
			string crYear = "";
			string crPrd = "";
			string[, ] VendorInvoices = null;
			int i = 0;
			int j = 0;
			int VchCount = 0;
			int VouchersImported = 0;
			int VouchersRejected = 0;
			bool HasGL = false;
			string newExpAmount = "";
			string newExpNarrative = "";
			string newExpBillNote = "";
			string newExpTaskCode = "";

			try
			{
				Cursor = Cursors.WaitCursor;

				UpdateStatus("Getting system info...", 1, 10);
				//First create a VoucherBatch and the DocTree location for it
				SQL = "SELECT     (SELECT SpNbrValue FROM SysParam WHERE SpName = 'CurAcctPrdYear') AS PrdYear, (SELECT SpNbrValue FROM SysParam WHERE SpName = 'CurAcctPrdNbr') AS PrdNbr";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");

				crYear = Convert.ToString(rsSys["PrdYear"]);
				crPrd = Convert.ToString(rsSys["PrdNbr"]);


				SQL = "SELECT SpNbrValue FROM SysParam WHERE SpName = 'LastBatchVoucher'";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				lastVchBatch = Convert.ToInt32(rsSys["SpNbrValue"]);
				lastVchBatch++;

				UpdateStatus("Creating new Voucher Batch...", 2, 10);
				SQL = "INSERT INTO VoucherBatch" + Environment.NewLine + 
				      "(VBBatchNbr,VBComment,VBBatchType,VBStatus,VBRecCount,VBUser,VBDate,VBJEBatchNbr,VBExpBatchNbr) " + Environment.NewLine + 
				      "VALUES  (" + lastVchBatch.ToString() + ", '" + txtBatchTitle.Text + " " + lastVchBatch.ToString() + " ', '1', 'L'," + Environment.NewLine + 
				      "         0,1,GetDate(),NULL,NULL)";

				DbCommand TempCommand = null;
				TempCommand = mJUTGlobal.Cn.CreateCommand();
				TempCommand.CommandText = SQL;
				TempCommand.ExecuteNonQuery();

				LogFile("Importing vouchers into new Voucher Batch " + lastVchBatch.ToString() + Environment.NewLine + "_________________________________________________" + Environment.NewLine);

				SQL = "UPDATE SysParam SET SpNbrValue = " + lastVchBatch.ToString() + " WHERE SpName = 'LastBatchVoucher'";
				DbCommand TempCommand_2 = null;
				TempCommand_2 = mJUTGlobal.Cn.CreateCommand();
				TempCommand_2.CommandText = SQL;
				TempCommand_2.ExecuteNonQuery();

				UpdateStatus("Creating Voucher Batch item in Juris...", 3, 10);
				Application.DoEvents();
				CreateVoucherBatchDocTree(lastVchBatch.ToString());



				//Now add vouchers to it one by one

				UpdateStatus("Reading vouchers from import file...", 4, 10);
				Application.DoEvents();
				//First add all vouchers to a local array
				i = 0;
				j = 1;
				rsVch.MoveFirst();
				VendorInvoices = ArraysHelper.InitializeArray<string[, ]>(new int[]{18, 2}, new int[]{0, 0});

				while(!rsVch.EOF)
				{
					if (Convert.ToString(rsVch["Type"]) == "A" || Convert.ToString(rsVch["Type"]) == "T")
					{
						VendorInvoices[0, i] = Convert.ToString(rsVch["VoucherDate"]);
						VendorInvoices[1, i] = Convert.ToString(rsVch["VendorCode"]);
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["PONbr"]))
						{
							VendorInvoices[2, i] = "";
						}
						else
						{
							VendorInvoices[2, i] = Convert.ToString(rsVch["PONbr"]);
						}
						VendorInvoices[3, i] = Convert.ToString(rsVch["InvoiceNbr"]);
						VendorInvoices[4, i] = Convert.ToString(rsVch["InvoiceDate"]);
						VendorInvoices[5, i] = Convert.ToString(rsVch["InvoiceAmt"]);
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["NonDiscAmt"]))
						{
							VendorInvoices[6, i] = "0";
						}
						else
						{
							VendorInvoices[6, i] = Convert.ToString(rsVch["NonDiscAmt"]);
						}
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["VchReference"]))
						{
							VendorInvoices[7, i] = "";
						}
						else
						{
							VendorInvoices[7, i] = Convert.ToString(rsVch["VchReference"]);
						}
						VendorInvoices[8, i] = "Y";
						//-----------Added for v1.1
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["DueDate"]))
						{
							VendorInvoices[9, i] = "";
						}
						else
						{
							VendorInvoices[9, i] = Convert.ToString(rsVch["DueDate"]);
						}
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["DiscountDate"]))
						{
							VendorInvoices[10, i] = "";
						}
						else
						{
							VendorInvoices[10, i] = Convert.ToString(rsVch["DiscountDate"]);
						}
						//------------Added for v2.1
						VendorInvoices[11, i] = Convert.ToString(rsVch["Type"]);
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["ExpClient"]))
						{
							VendorInvoices[12, i] = "";
						}
						else
						{
							VendorInvoices[12, i] = Convert.ToString(rsVch["ExpClient"]);
						}
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["ExpMatter"]))
						{
							VendorInvoices[13, i] = "";
						}
						else
						{
							VendorInvoices[13, i] = Convert.ToString(rsVch["ExpMatter"]);
						}
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["TrustBank"]))
						{
							VendorInvoices[14, i] = "";
						}
						else
						{
							VendorInvoices[14, i] = Convert.ToString(rsVch["TrustBank"]);
						}
						//------------Added for v2.1.2
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["SeparateCheck"]))
						{
							VendorInvoices[15, i] = "";
						}
						else
						{
							VendorInvoices[15, i] = Convert.ToString(rsVch["SeparateCheck"]);
						}
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsVch["APAcct"]))
						{
							VendorInvoices[16, i] = "";
						}
						else
						{
							VendorInvoices[16, i] = Convert.ToString(rsVch["APAcct"]);
						}
						i++;
						j++;
						VendorInvoices = ArraysHelper.RedimPreserve<string[, ]>(VendorInvoices, new int[]{18, j + 1});
					}
					rsVch.MoveNext();
				};
				VchCount = i;

				//Now go back and validate all the vouchers, checking if they exist already, if the vendor code is valid, if the client and matter are valid,
				//if the matter allows expenses, if the G/L account exists, if the expense code exists, if the date is valid, numbers are numbers, dates are dates
				VouchersImported = 0;
				VouchersRejected = 0;
				for (i = 0; i <= VchCount - 1; i++)
				{
					UpdateStatus("Validating Invoice " + VendorInvoices[3, i], i, VchCount);
					Application.DoEvents();
					if (!VendorIsValid(VendorInvoices[1, i]))
					{
						LogFile("Vendor " + VendorInvoices[1, i] + " for invoice " + VendorInvoices[3, i] + " is not a valid vendor code.  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					if (VendorInvoiceExists(VendorInvoices[1, i], VendorInvoices[3, i], VendorInvoices[4, i]) && optRejectDuplicates.Checked)
					{
						LogFile("Invoice #" + VendorInvoices[3, i] + " from vendor " + VendorInvoices[1, i] + " is a duplicate, and per indicated option will not be imported.  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					double dbNumericTemp = 0;
					if (!Double.TryParse(VendorInvoices[5, i], NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp))
					{
						LogFile("Invoice Amount " + VendorInvoices[5, i] + " for invoice " + VendorInvoices[3, i] + " is not numeric.  Check formatting!  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					double dbNumericTemp2 = 0;
					if (VendorInvoices[6, i] != "" && !Double.TryParse(VendorInvoices[6, i], NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp2))
					{
						LogFile("Non-discount Amount " + VendorInvoices[6, i] + " for invoice " + VendorInvoices[3, i] + " is not numeric.  Check formatting!  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					if (VendorInvoices[7, i] == "")
					{
						LogFile("Voucher reference is required for invoice " + VendorInvoices[3, i] + " .  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					if (!Information.IsDate(VendorInvoices[0, i]))
					{
						LogFile("Voucher Date " + VendorInvoices[0, i] + " for invoice " + VendorInvoices[3, i] + " is not a valid date format.  Check formatting!  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					if (!Information.IsDate(VendorInvoices[4, i]))
					{
						LogFile("Invoice Date " + VendorInvoices[4, i] + " for invoice " + VendorInvoices[3, i] + " is not a valid date format.  Check formatting!  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					if (VendorInvoices[9, i] != "" && !Information.IsDate(VendorInvoices[9, i]))
					{
						LogFile("Due Date " + VendorInvoices[9, i] + " for invoice " + VendorInvoices[3, i] + " is not a valid date.  Check formatting!  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}
					if (VendorInvoices[10, i] != "" && !Information.IsDate(VendorInvoices[10, i]))
					{
						LogFile("Discount Date " + VendorInvoices[10, i] + " for invoice " + VendorInvoices[3, i] + " is not a valid date.  Check formatting!  Voucher rejected.");
						VendorInvoices[8, i] = "N";
					}

					if (VendorInvoices[11, i] == "T")
					{
						if (!MatterIsValid(VendorInvoices[13, i], VendorInvoices[12, i]))
						{
							LogFile("Matter " + VendorInvoices[13, i] + " for trust invoice " + VendorInvoices[3, i] + " is not a valid matter code.  Voucher rejected.");
							VendorInvoices[8, i] = "N";
						}
						if (VendorInvoices[14, i] != "" && !MatterTrustBankIsValid(VendorInvoices[13, i], VendorInvoices[12, i], VendorInvoices[14, i]))
						{
							LogFile("Trust bank " + VendorInvoices[14, i] + " is not valid for matter " + VendorInvoices[12, i] + "/" + VendorInvoices[13, i] + ".  Voucher rejected.");
							VendorInvoices[8, i] = "N";
						}
						if (VendorInvoices[14, i] != "" && ((double) MatterTrustBankBalance(VendorInvoices[13, i], VendorInvoices[12, i], VendorInvoices[14, i])) < StringsHelper.ToDoubleSafe(VendorInvoices[5, i]))
						{
							LogFile("Trust bank " + VendorInvoices[14, i] + " has insufficient funds to cover invoice " + VendorInvoices[3, i] + ".  Voucher rejected.");
							VendorInvoices[8, i] = "N";
						}
						if (VendorInvoices[14, i] == "" && MatterTrustBank(VendorInvoices[13, i], VendorInvoices[12, i], Decimal.Parse(VendorInvoices[5, i], NumberStyles.Currency | NumberStyles.AllowExponent)) == "")
						{
							LogFile("Invoice " + VendorInvoices[3, i] + " does not specify a trust bank for matter " + VendorInvoices[12, i] + "/" + VendorInvoices[13, i] + ", and no trust account exists with sufficient funds to cover the invoice.  Voucher rejected.");
							VendorInvoices[8, i] = "N";
						}
					}
					else
					{
						//Check associated Expense and GL rows for this voucher
						rsVch.MoveFirst();
						HasGL = false;

						while(!rsVch.EOF)
						{
							if (Convert.ToString(rsVch["VendorCode"]) == VendorInvoices[1, i] && Convert.ToString(rsVch["InvoiceNbr"]) == VendorInvoices[3, i])
							{
								if (Convert.ToString(rsVch["Type"]) == "E")
								{
									if (!ClientIsValid(Convert.ToString(rsVch["ExpClient"])))
									{
										LogFile("Client " + Convert.ToString(rsVch["ExpClient"]) + " for invoice " + VendorInvoices[3, i] + " is not a valid client code.  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									if (!MatterIsValid(Convert.ToString(rsVch["ExpMatter"]), Convert.ToString(rsVch["ExpClient"])))
									{
										LogFile("Matter " + Convert.ToString(rsVch["ExpMatter"]) + " for invoice " + VendorInvoices[3, i] + " is not a valid matter code.  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									if (!MatterIsOpen(Convert.ToString(rsVch["ExpMatter"]), Convert.ToString(rsVch["ExpClient"])))
									{
										LogFile("Matter " + Convert.ToString(rsVch["ExpMatter"]) + " for invoice " + VendorInvoices[3, i] + " is currently closed.  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									if (!MatterAllowsExpenses(Convert.ToString(rsVch["ExpMatter"]), Convert.ToString(rsVch["ExpClient"])))
									{
										LogFile("Matter " + Convert.ToString(rsVch["ExpMatter"]) + " for invoice " + VendorInvoices[3, i] + " is set to disallow expenses.  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									if (!ExpCdIsValid(Convert.ToString(rsVch["ExpCode"])))
									{
										LogFile("Expense Code " + Convert.ToString(rsVch["ExpCode"]) + " for invoice " + VendorInvoices[3, i] + " is not a valid expense code.  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
									if (!Convert.IsDBNull(rsVch["ExpTaskCode"]))
									{
										if (!TaskCdIsValid(Convert.ToString(rsVch["ExpTaskCode"])))
										{
											LogFile("Expense Budget Task Code " + Convert.ToString(rsVch["ExpTaskCode"]) + " for invoice " + VendorInvoices[3, i] + " is not a valid expense code.  Voucher rejected.");
											VendorInvoices[8, i] = "N";
										}
									}
									double dbNumericTemp3 = 0;
									if (!Double.TryParse(Convert.ToString(rsVch["ExpUnits"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp3))
									{
										LogFile("Expense units " + Convert.ToString(rsVch["ExpUnits"]) + " for invoice " + VendorInvoices[3, i] + " is not numeric.  Check formatting!  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									double dbNumericTemp4 = 0;
									if (Convert.ToString(rsVch["ExpAmount"]) != "" && !Double.TryParse(Convert.ToString(rsVch["ExpAmount"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp4))
									{
										LogFile("Expense amount " + Convert.ToString(rsVch["ExpAmount"]) + " for invoice " + VendorInvoices[3, i] + " is not numeric.  Check formatting!  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
								}
								if (Convert.ToString(rsVch["Type"]) == "G")
								{
									if (!GLAcctIsValid(Convert.ToString(rsVch["GLDistAcct"])))
									{
										LogFile("G/L Account " + Convert.ToString(rsVch["GLDistAcct"]) + " for invoice " + VendorInvoices[3, i] + " is not a valid account number.  Check formatting!  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									double dbNumericTemp5 = 0;
									if (!Double.TryParse(Convert.ToString(rsVch["GLAmt"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp5))
									{
										LogFile("G/L Amount " + Convert.ToString(rsVch["GLAmt"]) + " for invoice " + VendorInvoices[3, i] + " is not numeric.  Check formatting!  Voucher rejected.");
										VendorInvoices[8, i] = "N";
									}
									HasGL = true;
								}
							}
							rsVch.MoveNext();
						};
						if (!HasGL)
						{ //No GL distribution is specified, so the Vendor MUST have a default GL Account
							if (!VendorHasDefaultGL(VendorInvoices[1, i]))
							{
								LogFile("There are no G/L distributions specified for invoice " + VendorInvoices[3, i] + ", and Vendor " + VendorInvoices[1, i] + " does not have a default G/L distribution account.  Voucher rejected.");
								VendorInvoices[8, i] = "N";
							}
						}
					}

					if (VendorInvoices[8, i] == "N")
					{
						VouchersRejected++;
					}
				}

				if (VouchersRejected >= 1)
				{
					if (VouchersRejected < VchCount)
					{
						if (MessageBox.Show("Out of " + VchCount.ToString() + " total vouchers in the import file, " + VouchersRejected.ToString() + " were rejected for one reason or another." + Environment.NewLine + 
						                    "The details are all in the log file.  Do you want to import the rest anyway?  Click Ok to import the remaining vouchers, or Cancel to exit without importing.", Application.ProductName, MessageBoxButtons.OKCancel) == System.Windows.Forms.DialogResult.Cancel)
						{
							MessageBox.Show("Import cancelled.", Application.ProductName);
							return;
						}
					}
					else
					{
						MessageBox.Show("It looks like all your vouchers were rejected, for one reason or another.  The details are all in the log file.  Import cancelled.", Application.ProductName);
						return;
					}
				}

				//We made it this far, so we're gonna import some vouchers baby woot!
				//Now go through the array one by one and add the vouchers with G/L and Expense, if they exist
				VouchersImported = 0;
				for (i = 0; i <= VchCount - 1; i++)
				{
					if (VendorInvoices[8, i] == "Y" && VendorInvoices[11, i] == "A")
					{ //Add an A/P Voucher
						HasGL = false;
						UpdateStatus("Creating A/P voucher for Invoice " + VendorInvoices[3, i], i, VchCount);
						Application.DoEvents();
						//Add the Voucher to the VoucherBatch
						RecNbr = AddVoucherToBatch(lastVchBatch.ToString(), VendorInvoices[0, i], VendorInvoices[1, i], VendorInvoices[2, i], VendorInvoices[3, i], VendorInvoices[4, i], VendorInvoices[5, i], VendorInvoices[6, i], VendorInvoices[7, i], VendorInvoices[9, i], VendorInvoices[10, i], VendorInvoices[15, i], VendorInvoices[16, i]);
						if (RecNbr < 0)
						{
							MessageBox.Show("There was an unexpected error adding invoice " + VendorInvoices[3, i] + ".  Voucher batch " + lastVchBatch.ToString() + " is in an unknown state and should be deleted.", Application.ProductName);
							return;
						}
						//See if there are G/L distributions in the Import file.  If there are, add them.  If not, apply it to default AP account.
						rsVch.MoveFirst();
						j = 1;

						while(!rsVch.EOF)
						{
							if (Convert.ToString(rsVch["Type"]) == "G" && Convert.ToString(rsVch["VendorCode"]) == VendorInvoices[1, i] && Convert.ToString(rsVch["InvoiceNbr"]) == VendorInvoices[3, i])
							{
								AddGLDist(lastVchBatch, RecNbr, j, Convert.ToString(rsVch["GLDistAcct"]), Convert.ToString(rsVch["GLAmt"]));
								HasGL = true;
								j++;
							}
							rsVch.MoveNext();
						};
						if (!HasGL)
						{
							AddDefaultGLDist(lastVchBatch, RecNbr, VendorInvoices[1, i], VendorInvoices[5, i]);
						}

						//See if there are Expense distributions in the Import file.  If there are, add them.  If not, do not add them.
						rsVch.MoveFirst();
						j = 1;

						while(!rsVch.EOF)
						{
							//LogFile "Type:" & rsVch!Type & ", RVendor: " & rsVch!VendorCode & ", AVendor: " & VendorInvoices(1, i) & ", RInvoice: " & rsVch!InvoiceNbr & ", AInvoice: " & VendorInvoices(3, i)
							if (Convert.ToString(rsVch["Type"]) == "E" && Convert.ToString(rsVch["VendorCode"]) == VendorInvoices[1, i] && Convert.ToString(rsVch["InvoiceNbr"]) == VendorInvoices[3, i])
							{
								//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
								if (Convert.IsDBNull(rsVch["ExpAmount"]))
								{
									newExpAmount = "";
								}
								else
								{
									newExpAmount = Convert.ToString(rsVch["ExpAmount"]);
								}
								//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
								if (Convert.IsDBNull(rsVch["ExpNarrative"]))
								{
									newExpNarrative = "";
								}
								else
								{
									newExpNarrative = Convert.ToString(rsVch["ExpNarrative"]);
								}
								//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
								if (Convert.IsDBNull(rsVch["ExpBillNote"]))
								{
									newExpBillNote = "";
								}
								else
								{
									newExpBillNote = Convert.ToString(rsVch["ExpBillNote"]);
								}
								//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
								if (Convert.IsDBNull(rsVch["ExpTaskCode"]))
								{
									newExpTaskCode = "";
								}
								else
								{
									newExpTaskCode = Convert.ToString(rsVch["ExpTaskCode"]);
								}
								AddExpDist(lastVchBatch, RecNbr, j, Convert.ToString(rsVch["ExpClient"]), Convert.ToString(rsVch["ExpMatter"]), Convert.ToString(rsVch["ExpCode"]), newExpTaskCode, Convert.ToString(rsVch["ExpUnits"]), newExpAmount, newExpNarrative, newExpBillNote, Convert.ToString(rsVch["VendorCode"]), Convert.ToString(rsVch["InvoiceNbr"]));
								j++;
							}
							rsVch.MoveNext();
						};
						VouchersImported++;
					}
					else if (VendorInvoices[8, i] == "Y" && VendorInvoices[11, i] == "T")
					{  //Add a Trust Voucher
						UpdateStatus("Creating Trust voucher for Invoice " + VendorInvoices[3, i], i, VchCount);
						Application.DoEvents();
						//Add the Voucher to the VoucherBatch
						RecNbr = AddTrustVoucherToBatch(lastVchBatch.ToString(), VendorInvoices[0, i], VendorInvoices[1, i], VendorInvoices[2, i], VendorInvoices[3, i], VendorInvoices[4, i], VendorInvoices[5, i], VendorInvoices[7, i], VendorInvoices[9, i], VendorInvoices[12, i], VendorInvoices[13, i], VendorInvoices[14, i]);
						if (RecNbr < 0)
						{
							MessageBox.Show("There was an unexpected error adding invoice " + VendorInvoices[3, i] + ".  Voucher batch " + lastVchBatch.ToString() + " is in an unknown state and should be deleted.", Application.ProductName);
							return;
						}
						VouchersImported++;
					}
					else
					{
						UpdateStatus("Skipping Invoice " + VendorInvoices[1, i], i, VchCount);
					}
				}

				SQL = "UPDATE VoucherBatch SET VBStatus = 'U' WHERE VBBatchNbr = " + lastVchBatch.ToString();
				DbCommand TempCommand_3 = null;
				TempCommand_3 = mJUTGlobal.Cn.CreateCommand();
				TempCommand_3.CommandText = SQL;
				TempCommand_3.ExecuteNonQuery();

				UpdateStatus("Ready", 1, 1);
				MessageBox.Show("Voucher Batch " + lastVchBatch.ToString() + " has been created with " + VouchersImported.ToString() + " vouchers, is currently unposted, and is ready for your review.", Application.ProductName);
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure ImportVouchers:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}



		}

		private void AddExpDist(int BatchNbr, int RecNbr, int SeqNbr, string ExpClient, string ExpMatter, string ExpCode, string ExpTaskCd, string ExpUnits, string ExpAmount, string ExpNarrative, string ExpBillNote, string VendorCode, string InvoiceNbr)
		{
			string SQL = "";
			ADORecordSetHelper rsDB = null;
			string newExpAmount = "";
			string newExpNarrative = "";
			string newExpBillNote = "";
			string amountSrc = "";

			try
			{
				SQL = "SELECT     Matter.MatSysNbr, Matter.MatExpSch, ISNULL(ExpSch.ESRMult, STDRExp.ESRMult) AS ESRMult, ISNULL(ExpSch.ESRSumry, STDRExp.ESRSumry) " + Environment.NewLine + 
				      "                      AS ESRSumry" + Environment.NewLine + 
				      "FROM         Client INNER JOIN" + Environment.NewLine + 
				      "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr LEFT OUTER JOIN" + Environment.NewLine + 
				      "                          (SELECT     ESRSch, ESRCode, ESRSumry, ESRShowUnits, ESRMult" + Environment.NewLine + 
				      "                            FROM          ExpSchRate AS ExpSchRate_1" + Environment.NewLine + 
				      "                            WHERE      (ESRCode = '" + ExpCode + "') AND (ESRSch = 'STDR')) AS ExpSch ON Matter.MatExpSch = ExpSch.ESRSch CROSS JOIN" + Environment.NewLine + 
				      "                          (SELECT     ESRSch, ESRCode, ESRSumry, ESRShowUnits, ESRMult" + Environment.NewLine + 
				      "                            FROM          ExpSchRate AS ExpSchRate_1" + Environment.NewLine + 
				      "                            WHERE      (ESRCode = '" + ExpCode + "') AND (ESRSch = 'STDR')) AS STDRExp" + Environment.NewLine + 
				      "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ExpClient + "', 12) AND" + Environment.NewLine + 
				      "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + ExpMatter + "', 12)";
				mJUTGlobal.Cmd.CommandText = SQL;
				//logfile sql
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");

				if (ExpAmount == "")
				{
					newExpAmount = (Double.Parse(ExpUnits) * Convert.ToDouble(rsDB["ESRMult"])).ToString();
					if (Convert.ToString(rsDB["MatExpSch"]) == "STDR")
					{
						amountSrc = "2";
					}
					else
					{
						amountSrc = "1";
					}
				}
				else
				{
					newExpAmount = ExpAmount;
					amountSrc = "U";
				}

				newExpNarrative = GetNarrative(ExpNarrative, VendorCode, InvoiceNbr);
				newExpBillNote = GetNote(ExpBillNote, VendorCode, InvoiceNbr);

				SQL = "INSERT INTO VoucherBatchMatDist (VBMBatch,VBMRecNbr,VBMSeqNbr,VBMExpBatch,VBMExpRecNbr,VBMMatter,VBMExpCd,VBMExpSched," + Environment.NewLine + 
				      "VBMUnits,VBMMult,VBMAmountSource,VBMAmount,VBMSummarize,VBMAuthBy,VBMBudgPhase,VBMBudgTaskCd,VBMCode1,VBMCode2,VBMCode3,VBMBillNote,VBMNarrative)" + Environment.NewLine + 
				      "VALUES (" + BatchNbr.ToString() + "," + RecNbr.ToString() + "," + SeqNbr.ToString() + ",NULL,NULL," + Convert.ToString(rsDB["MatSysNbr"]) + ",'" + ExpCode + "','" + Convert.ToString(rsDB["MatExpSch"]) + "'," + ExpUnits + "," + Environment.NewLine + 
				      "" + Convert.ToString(rsDB["ESRMult"]) + ",'" + amountSrc + "'," + newExpAmount + ",'" + Convert.ToString(rsDB["ESRSumry"]) + "',1,0,'" + ExpTaskCd + "','','','','" + Strings.Replace(newExpBillNote, "'", "", 1, -1, CompareMethod.Binary) + "'," + Environment.NewLine + 
				      "'" + Strings.Replace(newExpNarrative, "'", "", 1, -1, CompareMethod.Binary) + "')";
				//logfile sql
				DbCommand TempCommand = null;
				TempCommand = mJUTGlobal.Cn.CreateCommand();
				TempCommand.CommandText = SQL;
				TempCommand.ExecuteNonQuery();
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure AddExpDist:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}


		}

		private string GetNarrative(string oldNarrative, string VendorCode, string InvoiceNbr)
		{
			string SQL = "";
			ADORecordSetHelper rsDB = null;
			string narrative = "";


			if (oldNarrative == "")
			{
				SQL = "SELECT SpTxtValue FROM SysParam WHERE SpName = 'CfgVouchOpts'";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				narrative = "";
				if (TestRegExp("^.,.,.,Y.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
				{
					narrative = narrative + VendorCode;
				}
				if (TestRegExp("^.,.,.,.,Y.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
				{
					if (narrative != "")
					{
						narrative = narrative + "; " + GetVenName(VendorCode);
					}
					else
					{
						narrative = narrative + GetVenName(VendorCode);
					}
				}
				if (TestRegExp("^.,.,.,.,.,Y.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
				{
					if (narrative != "")
					{
						narrative = narrative + "; Invoice # " + InvoiceNbr;
					}
					else
					{
						narrative = narrative + "Invoice # " + InvoiceNbr;
					}
				}
			}
			else
			{
				narrative = oldNarrative;
			}
			return narrative;

		}

		private string GetNote(string oldNote, string VendorCode, string InvoiceNbr)
		{
			string SQL = "";
			ADORecordSetHelper rsDB = null;
			string note = "";


			if (oldNote == "")
			{
				SQL = "SELECT SpTxtValue FROM SysParam WHERE SpName = 'CfgVouchOpts'";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				note = "";
				if (TestRegExp("^.,.,.,.,.,.,Y.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
				{
					note = note + VendorCode;
				}
				if (TestRegExp("^.,.,.,.,.,.,.,Y.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
				{
					if (note != "")
					{
						note = note + "; " + GetVenName(VendorCode);
					}
					else
					{
						note = note + GetVenName(VendorCode);
					}
				}
				if (TestRegExp("^.,.,.,.,.,.,.,.,Y.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
				{
					if (note != "")
					{
						note = note + "; Invoice # " + InvoiceNbr;
					}
					else
					{
						note = note + "Invoice # " + InvoiceNbr;
					}
				}
			}
			else
			{
				note = oldNote;
			}
			return note;

		}

		private string GetVenName(string VenCode)
		{

			string SQL = "SELECT VenName FROM Vendor WHERE RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VenCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");

			return Convert.ToString(rsDB["VenName"]);
		}

		private void AddDefaultGLDist(int BatchNbr, int RecNbr, string VendorCode, string InvoiceAmt)
		{
			string SQL = "";
			ADORecordSetHelper rsDB = null;
			try
			{
				SQL = "SELECT * FROM Vendor WHERE RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VendorCode + "', 12)";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");

				SQL = "INSERT INTO VoucherBatchGLDist (VBGBatch,VBGRecNbr,VBGSeqNbr,VBGJEBatch,VBGJERecNbr,VBGGLAcct,VBGAmount)" + Environment.NewLine + 
				      "VALUES (" + BatchNbr.ToString() + "," + RecNbr.ToString() + ",1,NULL,NULL," + Convert.ToString(rsDB["VenDefaultDistAcct"]) + "," + InvoiceAmt + ")";
				//logfile sql
				DbCommand TempCommand = null;
				TempCommand = mJUTGlobal.Cn.CreateCommand();
				TempCommand.CommandText = SQL;
				TempCommand.ExecuteNonQuery();
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure AddDefaultGLDist:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}
		}

		private void AddGLDist(int BatchNbr, int RecNbr, int SeqNbr, string GLAcct, string GLAmount)
		{
			string SQL = "";
			ADORecordSetHelper rsDB = null;

			try
			{
				SQL = "SELECT ChtSysNbr FROM ChartOfAccounts WHERE (dbo.jfn_FormatChartOfAccount(ChtSysNbr) = '" + GLAcct + "')";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");

				SQL = "INSERT INTO VoucherBatchGLDist (VBGBatch,VBGRecNbr,VBGSeqNbr,VBGJEBatch,VBGJERecNbr,VBGGLAcct,VBGAmount)" + Environment.NewLine + 
				      "VALUES (" + BatchNbr.ToString() + "," + RecNbr.ToString() + "," + SeqNbr.ToString() + ",NULL,NULL," + Convert.ToString(rsDB["ChtSysNbr"]) + "," + GLAmount + ")";
				//logfile sql
				DbCommand TempCommand = null;
				TempCommand = mJUTGlobal.Cn.CreateCommand();
				TempCommand.CommandText = SQL;
				TempCommand.ExecuteNonQuery();
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure AddGLDist:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}
		}

		private int AddVoucherToBatch(string BatchNbr, string VoucherDate, string VendorCode, string PONbr, string InvoiceNbr, string InvoiceDate, string InvoiceAmt, string NonDiscAmt, string Reference, string DueDate, string DiscountDate, string SeparateCheck, string APAccount)
		{
			int result = 0;
			string SQL = "";
			ADORecordSetHelper rsDB = null;
			int i = 0;
			string newInvoice = "";
			int RecNbr = 0;
			int VoucherNbr = 0;
			int VendorSys = 0;
			int DueDays = 0;
			int DiscDays = 0;
			string Gets1099 = "";
			string SepCheck = "";
			int DiscAcct = 0;
			string APAcct = "";
			int recAffected = 0;
			string DefaultDisc = "";
			string DefAP = "";

			try
			{

				if (VendorInvoiceExists(VendorCode, InvoiceNbr, InvoiceDate))
				{
					i = 1;
					newInvoice = InvoiceNbr.Substring(0, Math.Min(18, InvoiceNbr.Length)) + "_";

					while(VendorInvoiceExists(VendorCode, newInvoice + i.ToString(), InvoiceDate))
					{
						i++;
					};
					newInvoice = newInvoice + i.ToString();
				}
				else
				{
					newInvoice = InvoiceNbr;
				}

				//Get RecNbr
				SQL = "exec qsVBDLastRecordNbrByBatch " + BatchNbr;
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				if (rsDB.EOF)
				{
					RecNbr = 1;
				}
				else
				{
					//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
					if (Convert.IsDBNull(rsDB["LastRecordNbr"]))
					{
						RecNbr = 1;
					}
					else
					{
						RecNbr = Convert.ToInt32(Convert.ToDouble(rsDB["LastRecordNbr"]) + 1);
					}
				}

				//Get Voucher Nbr
				SQL = "exec qsSysParamByName 'LastSysNbrVoucher'";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				VoucherNbr = Convert.ToInt32(rsDB["SpNbrValue"]);
				VoucherNbr++;

				SQL = "UPDATE SysParam SET SpNbrValue = " + VoucherNbr.ToString() + " WHERE SpName = 'LastSysNbrVoucher'";
				DbCommand TempCommand = null;
				TempCommand = mJUTGlobal.Cn.CreateCommand();
				TempCommand.CommandText = SQL;
				TempCommand.ExecuteNonQuery();

				SQL = "SELECT     SpTxtValue , (SELECT     TOP (1) APACode From APAccount ORDER BY APACode) AS DefAP From SysParam WHERE     (SpName = 'DefVenDiscAcct')";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				DefaultDisc = Convert.ToString(rsDB["SpTxtValue"]);
				DefAP = Convert.ToString(rsDB["DefAP"]);

				//Get Due Days, On1099, SeparateCheck, DiscAcct, APAcct
				SQL = "SELECT * FROM Vendor WHERE RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VendorCode + "', 12)";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				if (rsDB.EOF)
				{
					LogFile("Vendor " + VendorCode + " is not valid, and the associated invoice will not be imported.  Skipping.");
					return 0;
				}
				else
				{
					VendorSys = Convert.ToInt32(rsDB["VenSysNbr"]);
					DueDays = Convert.ToInt32(rsDB["VenDueDays"]);
					Gets1099 = Convert.ToString(rsDB["VenGets1099"]);
					if (SeparateCheck == "")
					{
						SepCheck = Convert.ToString(rsDB["VenSeparateCheck"]);
					}
					else
					{
						SepCheck = SeparateCheck;
					}
					//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
					if (Convert.IsDBNull(rsDB["VenDiscAcct"]))
					{
						DiscAcct = Convert.ToInt32(Double.Parse(DefaultDisc));
					}
					else
					{
						DiscAcct = Convert.ToInt32(rsDB["VenDiscAcct"]);
					}
					if (APAccount == "")
					{
						//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
						if (Convert.IsDBNull(rsDB["VenDefaultAPAcct"]))
						{
							APAcct = DefAP;
						}
						else
						{
							APAcct = Convert.ToString(rsDB["VenDefaultAPAcct"]);
						}
					}
					else
					{
						APAcct = APAccount;
					}
				}

				SQL = "INSERT INTO VoucherBatchDetail" + Environment.NewLine + 
				      "   (VBDBatch, VBDRecNbr, VBDType, VBDPosted, VBDVoucherNbr, VBDVoucherDate, VBDVendor, VBDMatter, VBDBank, VBDPONbr, VBDInvoiceNbr, VBDInvoiceDate, " + Environment.NewLine + 
				      "    VBDDueDate, VBDDiscDate, VBDInvoiceAmt, VBDNonDiscountAmt, VBDDiscAmt, VBDOn1099, VBDSeparateCheck, VBDDiscAcct, VBDAPAcct, VBDMemo)" + Environment.NewLine + 
				      "VALUES (" + BatchNbr + "," + RecNbr.ToString() + ",'A','N'," + VoucherNbr.ToString() + ",CONVERT(DATETIME, '" + VoucherDate + " 00:00:00', 102)," + VendorSys.ToString() + "," + Environment.NewLine + 
				      "        NULL,NULL,'" + PONbr + "','" + newInvoice + "',CONVERT(DATETIME, '" + InvoiceDate + " 00:00:00', 102),";
				if (DueDate == "")
				{
					SQL = SQL + "DATEADD(dd, " + DueDays.ToString() + ", CONVERT(DATETIME, '" + InvoiceDate + " 00:00:00', 102)),";
				}
				else
				{
					SQL = SQL + "CONVERT(DATETIME, '" + DueDate + " 00:00:00', 102),";
				}
				if (DiscountDate == "")
				{
					SQL = SQL + "DATEADD(dd, " + (DueDays - DiscDays).ToString() + ", CONVERT(DATETIME, '" + InvoiceDate + " 00:00:00', 102)),";
				}
				else
				{
					SQL = SQL + "CONVERT(DATETIME, '" + DiscountDate + " 00:00:00', 102),";
				}
				SQL = SQL + InvoiceAmt + "," + NonDiscAmt + ",0,'" + Gets1099 + "'," + Environment.NewLine + 
				      "        '" + SepCheck + "'," + DiscAcct.ToString() + ",'" + APAcct + "','" + Strings.Replace(Reference, "'", "", 1, -1, CompareMethod.Binary) + "')";
				//logfile sql
				DbCommand TempCommand_2 = null;
				TempCommand_2 = mJUTGlobal.Cn.CreateCommand();
				TempCommand_2.CommandText = SQL;
				recAffected = TempCommand_2.ExecuteNonQuery();



				if (recAffected < 1)
				{
					result = -1;
				}
				else
				{
					SQL = "UPDATE VoucherBatch SET VBRecCount = VBRecCount + 1 WHERE VBBatchNbr = " + BatchNbr;
					DbCommand TempCommand_3 = null;
					TempCommand_3 = mJUTGlobal.Cn.CreateCommand();
					TempCommand_3.CommandText = SQL;
					TempCommand_3.ExecuteNonQuery();
					result = RecNbr;
				}
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure AddVoucherToBatch:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}
			return result;
		}
		private int AddTrustVoucherToBatch(string BatchNbr, string VoucherDate, string VendorCode, string PONbr, string InvoiceNbr, string InvoiceDate, string InvoiceAmt, string Reference, string DueDate, string ClientCode, string MatterCode, string Bank)
		{
			int result = 0;
			string SQL = "";
			ADORecordSetHelper rsDB = null;
			int i = 0;
			string newInvoice = "";
			int RecNbr = 0;
			int VoucherNbr = 0;
			int VendorSys = 0;
			int DueDays = 0;
			string Gets1099 = "";
			string SepCheck = "";
			int recAffected = 0;
			string MatSys = "";

			try
			{

				if (VendorInvoiceExists(VendorCode, InvoiceNbr, InvoiceDate))
				{
					i = 1;
					newInvoice = InvoiceNbr.Substring(0, Math.Min(18, InvoiceNbr.Length)) + "_";

					while(VendorInvoiceExists(VendorCode, newInvoice + i.ToString(), InvoiceDate))
					{
						i++;
					};
					newInvoice = newInvoice + i.ToString();
				}
				else
				{
					newInvoice = InvoiceNbr;
				}

				//Get RecNbr
				SQL = "exec qsVBDLastRecordNbrByBatch " + BatchNbr;
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				if (rsDB.EOF)
				{
					RecNbr = 1;
				}
				else
				{
					//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
					if (Convert.IsDBNull(rsDB["LastRecordNbr"]))
					{
						RecNbr = 1;
					}
					else
					{
						RecNbr = Convert.ToInt32(Convert.ToDouble(rsDB["LastRecordNbr"]) + 1);
					}
				}

				//Get Voucher Nbr
				SQL = "exec qsSysParamByName 'LastSysNbrVoucher'";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				VoucherNbr = Convert.ToInt32(rsDB["SpNbrValue"]);
				VoucherNbr++;

				SQL = "UPDATE SysParam SET SpNbrValue = " + VoucherNbr.ToString() + " WHERE SpName = 'LastSysNbrVoucher'";
				DbCommand TempCommand = null;
				TempCommand = mJUTGlobal.Cn.CreateCommand();
				TempCommand.CommandText = SQL;
				TempCommand.ExecuteNonQuery();

				SQL = "SELECT     Matter.MatSysNbr" + Environment.NewLine + 
				      "FROM         Client INNER JOIN" + Environment.NewLine + 
				      "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr" + Environment.NewLine + 
				      "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
				      "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12)";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				MatSys = Convert.ToString(rsDB["MatSysNbr"]);

				//If Bank is not specified, get Bank
				if (Bank == "")
				{
					Bank = MatterTrustBank(MatterCode, ClientCode, Decimal.Parse(InvoiceAmt, NumberStyles.Currency | NumberStyles.AllowExponent));
				}


				//Get Due Days, On1099, SeparateCheck, DiscAcct, APAcct
				SQL = "SELECT * FROM Vendor WHERE RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VendorCode + "', 12)";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				if (rsDB.EOF)
				{
					LogFile("Vendor " + VendorCode + " is not valid, and the associated invoice will not be imported.  Skipping.");
					return 0;
				}
				else
				{
					VendorSys = Convert.ToInt32(rsDB["VenSysNbr"]);
					DueDays = Convert.ToInt32(rsDB["VenDueDays"]);
					Gets1099 = Convert.ToString(rsDB["VenGets1099"]);
					SepCheck = Convert.ToString(rsDB["VenSeparateCheck"]);
				}

				SQL = "INSERT INTO VoucherBatchDetail" + Environment.NewLine + 
				      "   (VBDBatch, VBDRecNbr, VBDType, VBDPosted, VBDVoucherNbr, VBDVoucherDate, VBDVendor, VBDMatter, VBDBank, VBDPONbr, VBDInvoiceNbr, VBDInvoiceDate, " + Environment.NewLine + 
				      "    VBDDueDate, VBDDiscDate, VBDInvoiceAmt, VBDNonDiscountAmt, VBDDiscAmt, VBDOn1099, VBDSeparateCheck, VBDDiscAcct, VBDAPAcct, VBDMemo)" + Environment.NewLine + 
				      "VALUES (" + BatchNbr + "," + RecNbr.ToString() + ",'T','N'," + VoucherNbr.ToString() + ",CONVERT(DATETIME, '" + VoucherDate + " 00:00:00', 102)," + VendorSys.ToString() + "," + Environment.NewLine + 
				      "" + MatSys + ",'" + Bank + "','" + PONbr + "','" + newInvoice + "',CONVERT(DATETIME, '" + InvoiceDate + " 00:00:00', 102),";
				if (DueDate == "")
				{
					SQL = SQL + "DATEADD(dd, " + DueDays.ToString() + ", CONVERT(DATETIME, '" + InvoiceDate + " 00:00:00', 102)),";
				}
				else
				{
					SQL = SQL + "CONVERT(DATETIME, '" + DueDate + " 00:00:00', 102),";
				}
				SQL = SQL + "CONVERT(DATETIME, '1/1/1900 00:00:00', 102)," + InvoiceAmt + ",0,0,'" + Gets1099 + "'," + Environment.NewLine + 
				      "        '" + SepCheck + "',NULL,NULL,'" + Strings.Replace(Reference, "'", "", 1, -1, CompareMethod.Binary) + "')";
				//LogFile SQL
				DbCommand TempCommand_2 = null;
				TempCommand_2 = mJUTGlobal.Cn.CreateCommand();
				TempCommand_2.CommandText = SQL;
				recAffected = TempCommand_2.ExecuteNonQuery();



				if (recAffected < 1)
				{
					result = -1;
				}
				else
				{
					SQL = "UPDATE VoucherBatch SET VBRecCount = VBRecCount + 1 WHERE VBBatchNbr = " + BatchNbr;
					DbCommand TempCommand_3 = null;
					TempCommand_3 = mJUTGlobal.Cn.CreateCommand();
					TempCommand_3.CommandText = SQL;
					TempCommand_3.ExecuteNonQuery();
					result = RecNbr;
				}
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure AddTrustVoucherToBatch:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}
			return result;
		}


		private bool VendorHasDefaultGL(string VendorCode)
		{

			string SQL = "SELECT * FROM Vendor WHERE RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VendorCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
			if (Convert.IsDBNull(rsDB["VenDefaultDistAcct"]))
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool VendorInvoiceExists(string VendorCode, string InvoiceNbr, string InvoiceDate)
		{

			string SQL = "SELECT     Vendor.VenCode, Voucher.VchInvoiceNbr" + Environment.NewLine + 
			             "FROM         Voucher INNER JOIN" + Environment.NewLine + 
			             "                      Vendor ON Voucher.VchVendor = Vendor.VenSysNbr" + Environment.NewLine + 
			             "WHERE     RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VendorCode + "', 12) AND (Voucher.VchInvoiceNbr = '" + InvoiceNbr + "') AND " + Environment.NewLine + 
			             "          (Voucher.VchInvoiceDate = CONVERT(DATETIME, '" + InvoiceDate + " 00:00:00', 102))";
			mJUTGlobal.Cmd.CommandText = SQL;
			//LogFile SQL
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool MatterIsOpen(string MatterCode, string ClientCode)
		{

			string SQL = "SELECT     Matter.MatStatusFlag" + Environment.NewLine + 
			             "FROM         Client INNER JOIN" + Environment.NewLine + 
			             "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
			             "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			return Convert.ToString(rsDB["MatStatusFlag"]) == "O";
		}

		private bool MatterAllowsExpenses(string MatterCode, string ClientCode)
		{

			string SQL = "SELECT     Matter.MatLockFlag" + Environment.NewLine + 
			             "FROM         Client INNER JOIN" + Environment.NewLine + 
			             "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
			             "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			if (Convert.ToDouble(rsDB["MatLockFlag"]) > 1)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool MatterIsValid(string MatterCode, string ClientCode)
		{

			string SQL = "SELECT     Matter.MatSysNbr" + Environment.NewLine + 
			             "FROM         Client INNER JOIN" + Environment.NewLine + 
			             "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
			             "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				//MsgBox (rsDB!MatSysNbr)
				return false;
			}
			else
			{
				//MsgBox (rsDB!MatSysNbr)
				return true;
			}
		}

		private bool ClientIsValid(string ClientCode)
		{

			string SQL = "SELECT     Client.CliSysNbr" + Environment.NewLine + 
			             "FROM         Client" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool GLAcctIsValid(string GLAcct)
		{

			string SQL = "SELECT     ChtSysNbr FROM ChartOfAccounts WHERE (dbo.jfn_FormatChartOfAccount(ChtSysNbr) = '" + GLAcct + "')";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool MatterTrustBankIsValid(string MatterCode, string ClientCode, string Bank)
		{

			string SQL = "SELECT     TrustAccount.TABank" + Environment.NewLine + 
			             "FROM         Client INNER JOIN" + Environment.NewLine + 
			             "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr INNER JOIN" + Environment.NewLine + 
			             "                      TrustAccount ON TrustAccount.TAMatter = Matter.MatSysNbr" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
			             "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12) AND" + Environment.NewLine + 
			             "      (TrustAccount.TABank = '" + Bank + "')";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private string MatterTrustBank(string MatterCode, string ClientCode, decimal InvoiceAmt)
		{

			string SQL = "SELECT     TrustAccount.TABank" + Environment.NewLine + 
			             "FROM         Client INNER JOIN" + Environment.NewLine + 
			             "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr INNER JOIN" + Environment.NewLine + 
			             "                      TrustAccount ON TrustAccount.TAMatter = Matter.MatSysNbr" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
			             "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12) AND" + Environment.NewLine + 
			             "      (TrustAccount.TABalance >= " + InvoiceAmt.ToString() + ")";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return "";
			}
			else
			{
				return Convert.ToString(rsDB["TABank"]);
			}
		}

		private decimal MatterTrustBankBalance(string MatterCode, string ClientCode, string Bank)
		{

			string SQL = "SELECT     TrustAccount.TABalance" + Environment.NewLine + 
			             "FROM         Client INNER JOIN" + Environment.NewLine + 
			             "                      Matter ON Client.CliSysNbr = Matter.MatCliNbr INNER JOIN" + Environment.NewLine + 
			             "                      TrustAccount ON TrustAccount.TAMatter = Matter.MatSysNbr" + Environment.NewLine + 
			             "WHERE RIGHT('000000000000' + Client.CliCode, 12) = RIGHT('000000000000' + '" + ClientCode + "', 12) AND" + Environment.NewLine + 
			             "      RIGHT('000000000000' + Matter.MatCode, 12) = RIGHT('000000000000' + '" + MatterCode + "', 12) AND" + Environment.NewLine + 
			             "      (TrustAccount.TABank = '" + Bank + "')";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return 0;
			}
			else
			{
				return Convert.ToDecimal(rsDB["TABalance"]);
			}
		}

		private bool ExpCdIsValid(string ExpCd)
		{

			string SQL = "SELECT     ExpCdCode FROM ExpenseCode WHERE (ExpCdCode = '" + ExpCd + "')";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool TaskCdIsValid(string TaskCd)
		{

			string SQL = "SELECT     TaskCdCode FROM TaskCode WHERE (TaskCdCode = '" + TaskCd + "')";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool APAcctIsValid(string APAcct)
		{

			string SQL = "SELECT     APACode FROM APAccount WHERE (APACode = '" + APAcct + "')";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private bool VendorIsValid(string VendorCode)
		{

			string SQL = "SELECT * FROM Vendor WHERE RIGHT('000000000000' + VenCode, 12) = RIGHT('000000000000' + '" + VendorCode + "', 12)";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			if (rsDB.EOF)
			{
				return false;
			}
			else
			{
				return true;
			}
		}

		private int GetDocTreeFolderOption()
		{

			string SQL = "SELECT SpTxtValue FROM SysParam WHERE SpName = 'CfgMiscOpts'";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsDB = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");

			if (TestRegExp("^\\d*,.,.,.,.,.,0.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
			{
				return 0;
			}
			else if (TestRegExp("^\\d*,.,.,.,.,.,1.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
			{ 
				return 1;
			}
			else if (TestRegExp("^\\d*,.,.,.,.,.,2.*", Convert.ToString(rsDB["SpTxtValue"])) == Convert.ToString(rsDB["SpTxtValue"]))
			{ 
				return 2;
			}
			else
			{
				return -1;
			}

		}

		private void CreateVoucherBatchDocTree(string BatchNum)
		{
			string SQL = "";
			ADORecordSetHelper rsSys = null;
			int newDocTree = 0;
			string docTreeTitle = "";
			int docTreeParent = 0;
			int docIDTemp = 0;
			//v1.0.3 - Now puts the new batch in the current accounting period folder, instead of today's date

			try
			{

				SQL = "SELECT (SELECT SpNbrValue FROM SysParam WHERE SpName = 'LastSysNbrDocTree') AS LastSysNbrDocTree," + Environment.NewLine + 
				      "       (SELECT CAST(SpNbrValue as CHAR(4)) FROM SysParam WHERE SpName = 'CurAcctPrdYear') + '-' +" + Environment.NewLine + 
				      "       (SELECT RIGHT('00' + CAST(SpNbrValue as varchar(2)), 2) FROM SysParam WHERE SpName = 'CurAcctPrdNbr') AS DocTreeTitle";

				mJUTGlobal.Cmd.CommandText = SQL;
				rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				newDocTree = Convert.ToInt32(rsSys["LastSysNbrDocTree"]);
				newDocTree++;
				docTreeTitle = Convert.ToString(rsSys["docTreeTitle"]);

				SQL = "exec qsDocumentTreeRootFldByCls 7200";
				mJUTGlobal.Cmd.CommandText = SQL;
				rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
				docIDTemp = Convert.ToInt32(rsSys["DTDocID"]); //Holds value in case the query returns EOF

				switch(GetDocTreeFolderOption())
				{
					case 0 : 
						//Dump it in the main CashReceipt folder 
						docTreeParent = Convert.ToInt32(rsSys["DTDocID"]); 
						break;
					case 1 : 
						//Put it in the User folder, then accounting period 
						SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + Convert.ToString(rsSys["DTDocID"]) + ",'SMGR'"; 
						mJUTGlobal.Cmd.CommandText = SQL; 
						rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, ""); 
						if (rsSys.EOF)
						{
							SQL = "INSERT INTO DocumentTree ( DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)" + Environment.NewLine + 
							      "VALUES (" + newDocTree.ToString() + ",'Y',7200,'F'," + docIDTemp.ToString() + ",'SMGR',NULL,NULL)";
							DbCommand TempCommand = null;
							TempCommand = mJUTGlobal.Cn.CreateCommand();
							TempCommand.CommandText = SQL;
							TempCommand.ExecuteNonQuery();
							SQL = "UPDATE SysParam SET SpNbrValue = SpNbrValue + 1 WHERE SpName = 'LastSysNbrDocTree'";
							DbCommand TempCommand_2 = null;
							TempCommand_2 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_2.CommandText = SQL;
							TempCommand_2.ExecuteNonQuery();
							newDocTree++;
							SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + docIDTemp.ToString() + ",'SMGR'";
							mJUTGlobal.Cmd.CommandText = SQL;
							rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
							docIDTemp = Convert.ToInt32(rsSys["DTDocID"]);
						}
						else
						{
							docIDTemp = Convert.ToInt32(rsSys["DTDocID"]);
						} 
						SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + Convert.ToString(rsSys["DTDocID"]) + ",'" + docTreeTitle + "'"; 
						mJUTGlobal.Cmd.CommandText = SQL; 
						rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, ""); 
						if (rsSys.EOF)
						{
							SQL = "INSERT INTO DocumentTree ( DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)" + Environment.NewLine + 
							      "VALUES (" + newDocTree.ToString() + ",'Y',7200,'F'," + docIDTemp.ToString() + ",'" + docTreeTitle + "',NULL,NULL)";
							DbCommand TempCommand_3 = null;
							TempCommand_3 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_3.CommandText = SQL;
							TempCommand_3.ExecuteNonQuery();
							SQL = "UPDATE SysParam SET SpNbrValue = SpNbrValue + 1 WHERE SpName = 'LastSysNbrDocTree'";
							DbCommand TempCommand_4 = null;
							TempCommand_4 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_4.CommandText = SQL;
							TempCommand_4.ExecuteNonQuery();
							newDocTree++;
							SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + docIDTemp.ToString() + ",'" + docTreeTitle + "'";
							mJUTGlobal.Cmd.CommandText = SQL;
							rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
							docIDTemp = Convert.ToInt32(rsSys["DTDocID"]);
						} 
						docTreeParent = Convert.ToInt32(rsSys["DTDocID"]); 
						break;
					case 2 : 
						//Put it in the accounting period, then user folder 
						SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + Convert.ToString(rsSys["DTDocID"]) + ",'" + docTreeTitle + "'"; 
						mJUTGlobal.Cmd.CommandText = SQL; 
						rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, ""); 
						if (rsSys.EOF)
						{
							SQL = "INSERT INTO DocumentTree ( DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)" + Environment.NewLine + 
							      "VALUES (" + newDocTree.ToString() + ",'Y',7200,'F'," + docIDTemp.ToString() + ",'" + docTreeTitle + "',NULL,NULL)";
							DbCommand TempCommand_5 = null;
							TempCommand_5 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_5.CommandText = SQL;
							TempCommand_5.ExecuteNonQuery();
							SQL = "UPDATE SysParam SET SpNbrValue = SpNbrValue + 1 WHERE SpName = 'LastSysNbrDocTree'";
							DbCommand TempCommand_6 = null;
							TempCommand_6 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_6.CommandText = SQL;
							TempCommand_6.ExecuteNonQuery();
							newDocTree++;
							SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + docIDTemp.ToString() + ",'" + docTreeTitle + "'";
							mJUTGlobal.Cmd.CommandText = SQL;
							rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
							docIDTemp = Convert.ToInt32(rsSys["DTDocID"]);
						}
						else
						{
							docIDTemp = Convert.ToInt32(rsSys["DTDocID"]);
						} 
						SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + Convert.ToString(rsSys["DTDocID"]) + ",'SMGR'"; 
						mJUTGlobal.Cmd.CommandText = SQL; 
						rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, ""); 
						if (rsSys.EOF)
						{
							SQL = "INSERT INTO DocumentTree ( DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)" + Environment.NewLine + 
							      "VALUES (" + newDocTree.ToString() + ",'Y',7200,'F'," + docIDTemp.ToString() + ",'SMGR',NULL,NULL)";
							DbCommand TempCommand_7 = null;
							TempCommand_7 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_7.CommandText = SQL;
							TempCommand_7.ExecuteNonQuery();
							SQL = "UPDATE SysParam SET SpNbrValue = SpNbrValue + 1 WHERE SpName = 'LastSysNbrDocTree'";
							DbCommand TempCommand_8 = null;
							TempCommand_8 = mJUTGlobal.Cn.CreateCommand();
							TempCommand_8.CommandText = SQL;
							TempCommand_8.ExecuteNonQuery();
							newDocTree++;
							SQL = "exec qsDocumentTreeByClassTypeParentIDTitle 7200,'F'," + docIDTemp.ToString() + ",'SMGR'";
							mJUTGlobal.Cmd.CommandText = SQL;
							rsSys = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
							docIDTemp = Convert.ToInt32(rsSys["DTDocID"]);
						} 
						docTreeParent = Convert.ToInt32(rsSys["DTDocID"]); 
						break;
					default:
						MessageBox.Show("There was an error determining Firm Option for placing new batch folders.", Application.ProductName); 
						 
						break;
				}

				SQL = "INSERT INTO DocumentTree ( DTDocID, DTSystemCreated, DTDocClass, DTDocType, DTParentID, DTTitle, DTKeyL, DTKeyT)" + Environment.NewLine + 
				      "VALUES (" + newDocTree.ToString() + ",'Y',7200,'R'," + docTreeParent.ToString() + ",'" + txtBatchTitle.Text + " " + BatchNum + " '," + BatchNum + ",NULL)";
				DbCommand TempCommand_9 = null;
				TempCommand_9 = mJUTGlobal.Cn.CreateCommand();
				TempCommand_9.CommandText = SQL;
				TempCommand_9.ExecuteNonQuery();
				SQL = "UPDATE SysParam SET SpNbrValue = SpNbrValue + 1 WHERE SpName = 'LastSysNbrDocTree'";
				DbCommand TempCommand_10 = null;
				TempCommand_10 = mJUTGlobal.Cn.CreateCommand();
				TempCommand_10.CommandText = SQL;
				TempCommand_10.ExecuteNonQuery();
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure CreateVoucherBatchDocTree:" + Environment.NewLine + excep.Message, Application.ProductName);
				LogFile(SQL);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}


		}

		private bool RecordSetIsValid(ADORecordSetHelper rs)
		{
			bool result = false;
			string MissingField = "";
			int i = 0;
			int j = 0;
			decimal GLDist = 0;
			decimal ExpDist = 0;
			int VchCount = 0;
			bool VoucherExists = false;
			string[, ] VendorInvoices = null;
			bool MissingVenInv = false;
			try
			{

				UpdateStatus("Checking required fields...", 1, 5);
				Application.DoEvents();
				result = true;
				MissingVenInv = false;
				//First check to see if it has all the right fields
				MissingField = "";
				if (!FieldExistsInRS(rs, "Type"))
				{
					MissingField = MissingField + "Type, ";
				}
				if (!FieldExistsInRS(rs, "VoucherDate"))
				{
					MissingField = MissingField + "VoucherDate, ";
				}
				if (!FieldExistsInRS(rs, "VendorCode"))
				{
					MissingField = MissingField + "VendorCode, ";
				}
				if (!FieldExistsInRS(rs, "PONbr"))
				{
					MissingField = MissingField + "PONbr, ";
				}
				if (!FieldExistsInRS(rs, "InvoiceNbr"))
				{
					MissingField = MissingField + "InvoiceNbr, ";
				}
				if (!FieldExistsInRS(rs, "InvoiceDate"))
				{
					MissingField = MissingField + "InvoiceDate, ";
				}
				if (!FieldExistsInRS(rs, "DueDate"))
				{
					MissingField = MissingField + "DueDate, ";
				}
				if (!FieldExistsInRS(rs, "DiscountDate"))
				{
					MissingField = MissingField + "DiscountDate, ";
				}
				if (!FieldExistsInRS(rs, "InvoiceAmt"))
				{
					MissingField = MissingField + "InvoiceAmt, ";
				}
				if (!FieldExistsInRS(rs, "NonDiscAmt"))
				{
					MissingField = MissingField + "NonDiscAmt, ";
				}
				if (!FieldExistsInRS(rs, "VchReference"))
				{
					MissingField = MissingField + "VchReference, ";
				}
				if (!FieldExistsInRS(rs, "SeparateCheck"))
				{
					MissingField = MissingField + "SeparateCheck, ";
				}
				if (!FieldExistsInRS(rs, "APAcct"))
				{
					MissingField = MissingField + "APAcct, ";
				}
				if (!FieldExistsInRS(rs, "GLDistAcct"))
				{
					MissingField = MissingField + "GLDistAcct, ";
				}
				if (!FieldExistsInRS(rs, "GLAmt"))
				{
					MissingField = MissingField + "GLAmt, ";
				}
				if (!FieldExistsInRS(rs, "TrustBank"))
				{
					MissingField = MissingField + "TrustBank, ";
				}
				if (!FieldExistsInRS(rs, "ExpClient"))
				{
					MissingField = MissingField + "ExpClient, ";
				}
				if (!FieldExistsInRS(rs, "ExpMatter"))
				{
					MissingField = MissingField + "ExpMatter, ";
				}
				if (!FieldExistsInRS(rs, "ExpCode"))
				{
					MissingField = MissingField + "ExpCode, ";
				}
				if (!FieldExistsInRS(rs, "ExpTaskCode"))
				{
					MissingField = MissingField + "ExpTaskCode, ";
				}
				if (!FieldExistsInRS(rs, "ExpUnits"))
				{
					MissingField = MissingField + "ExpUnits, ";
				}
				if (!FieldExistsInRS(rs, "ExpAmount"))
				{
					MissingField = MissingField + "ExpAmount, ";
				}
				if (!FieldExistsInRS(rs, "ExpNarrative"))
				{
					MissingField = MissingField + "ExpNarrative, ";
				}
				if (!FieldExistsInRS(rs, "ExpBillNote"))
				{
					MissingField = MissingField + "ExpBillNote, ";
				}
				if (MissingField != "")
				{
					MessageBox.Show("Error: The CSV file does not meet file format specifications.  It is missing fields " + MissingField + " and may have other issues." + Environment.NewLine + Environment.NewLine + 
					                "Please refer to specifications document 'VoucherImport_CSV_Specs.v2.1.2.pdf'.", Application.ProductName);
					return false;
				}

				UpdateStatus("Checking field contents...", 2, 5);
				Application.DoEvents();
				//Now check all the rows, and make sure they have all the required fields, by type
				rs.MoveFirst();
				i = 2;

				while(!rs.EOF)
				{
					//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
					if (Convert.IsDBNull(rs["VendorCode"]) || Convert.ToString(rs["VendorCode"]) == "")
					{
						LogFile("Error on line " + i.ToString() + ": Missing or Invalid VendorCode (" + Convert.ToString(rs["VendorCode"]) + ")." + Environment.NewLine);
						result = false;
						MissingVenInv = true;
					}
					//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx
					if (Convert.IsDBNull(rs["InvoiceNbr"]) || Convert.ToString(rs["InvoiceNbr"]) == "")
					{
						LogFile("Error on line " + i.ToString() + ": Missing or Invalid InvoiceNbr (" + Convert.ToString(rs["InvoiceNbr"]) + ")." + Environment.NewLine);
						result = false;
						MissingVenInv = true;
					}
					switch(Convert.ToString(rs["Type"]))
					{
						case "A" : 
							if (!Information.IsDate(rs["VoucherDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or Invalid VoucherDate (" + Convert.ToString(rs["VoucherDate"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							if (Strings.Len(Convert.ToString(rs["PONbr"])) > 20)
							{
								LogFile("Error on line " + i.ToString() + ": PONbr is too long (20 char max) (" + Convert.ToString(rs["PONbr"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							if (!Information.IsDate(rs["InvoiceDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or Invalid InvoiceDate (" + Convert.ToString(rs["InvoiceDate"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (!Convert.IsDBNull(rs["DueDate"]) && Convert.ToString(rs["DueDate"]) != "" && !Information.IsDate(rs["DueDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Due Date " + Convert.ToString(rs["DueDate"]) + " is not a valid date.  Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (!Convert.IsDBNull(rs["DiscountDate"]) && Convert.ToString(rs["DiscountDate"]) != "" && !Information.IsDate(rs["DiscountDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Discount Date " + Convert.ToString(rs["DiscountDate"]) + " is not a valid date.  Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							double dbNumericTemp = 0; 
							if (!Double.TryParse(Convert.ToString(rs["InvoiceAmt"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or Invalid InvoiceAmt (" + Convert.ToString(rs["InvoiceAmt"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							double dbNumericTemp2 = 0; 
							if (Convert.ToString(rs["NonDiscAmt"]) != "" && !Double.TryParse(Convert.ToString(rs["NonDiscAmt"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp2))
							{
								LogFile("Error on line " + i.ToString() + ": Invalid NonDiscAmt (" + Convert.ToString(rs["NonDiscAmt"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["VchReference"]) || Convert.ToString(rs["VchReference"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing VchReference (" + Convert.ToString(rs["VchReference"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (!Convert.IsDBNull(rs["APAcct"]))
							{
								if (!APAcctIsValid(Convert.ToString(rs["APAcct"])))
								{
									LogFile("Error on line " + i.ToString() + ": Invalid APAcct (" + Convert.ToString(rs["APAcct"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
									result = false;
								}
							} 
							break;
						case "T" : 
							if (!Information.IsDate(rs["VoucherDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or Invalid VoucherDate (" + Convert.ToString(rs["VoucherDate"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							if (Strings.Len(Convert.ToString(rs["PONbr"])) > 20)
							{
								LogFile("Error on line " + i.ToString() + ": PONbr is too long (20 char max) (" + Convert.ToString(rs["PONbr"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							if (!Information.IsDate(rs["InvoiceDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or Invalid InvoiceDate (" + Convert.ToString(rs["InvoiceDate"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (!Convert.IsDBNull(rs["DueDate"]) && Convert.ToString(rs["DueDate"]) != "" && !Information.IsDate(rs["DueDate"]))
							{
								LogFile("Error on line " + i.ToString() + ": Due Date " + Convert.ToString(rs["DueDate"]) + " is not a valid date.  Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							double dbNumericTemp3 = 0; 
							if (!Double.TryParse(Convert.ToString(rs["InvoiceAmt"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp3))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or Invalid InvoiceAmt (" + Convert.ToString(rs["InvoiceAmt"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["VchReference"]) || Convert.ToString(rs["VchReference"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing VchReference (" + Convert.ToString(rs["VchReference"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["ExpClient"]) || Convert.ToString(rs["ExpClient"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing ExpClient (" + Convert.ToString(rs["ExpClient"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["ExpMatter"]) || Convert.ToString(rs["ExpMatter"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing ExpMatter (" + Convert.ToString(rs["ExpMatter"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							break;
						case "G" : 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["GLDistAcct"]) || Convert.ToString(rs["GLDistAcct"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing GLDistAcct (" + Convert.ToString(rs["GLDistAcct"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["GLAmt"]))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or invalid GLAmt (" + Convert.ToString(rs["GLAmt"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							double dbNumericTemp4 = 0; 
							if (!Double.TryParse(Convert.ToString(rs["GLAmt"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp4))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or invalid GLAmt (" + Convert.ToString(rs["GLAmt"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							break;
						case "E" : 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["ExpClient"]) || Convert.ToString(rs["ExpClient"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing ExpClient (" + Convert.ToString(rs["ExpClient"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["ExpMatter"]) || Convert.ToString(rs["ExpMatter"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing ExpMatter (" + Convert.ToString(rs["ExpMatter"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
							if (Convert.IsDBNull(rs["ExpCode"]) || Convert.ToString(rs["ExpCode"]) == "")
							{
								LogFile("Error on line " + i.ToString() + ": Missing ExpCode (" + Convert.ToString(rs["ExpCode"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							double dbNumericTemp5 = 0; 
							if (!Double.TryParse(Convert.ToString(rs["ExpUnits"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp5))
							{
								LogFile("Error on line " + i.ToString() + ": Missing or invalid ExpUnits (" + Convert.ToString(rs["ExpUnits"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							double dbNumericTemp6 = 0; 
							if (Convert.ToString(rs["ExpAmount"]) != "" && !Double.TryParse(Convert.ToString(rs["ExpAmount"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp6))
							{
								LogFile("Error on line " + i.ToString() + ": Invalid ExpAmount (" + Convert.ToString(rs["ExpAmount"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							if (Strings.Len(Convert.ToString(rs["ExpBillNote"])) > 60)
							{
								LogFile("Error on line " + i.ToString() + ": ExpBillNote is too long (60 char max) (" + Convert.ToString(rs["ExpBillNote"]) + "): Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							} 
							break;
						default:
							LogFile("Error on line " + i.ToString() + ": Record type must be A, T, G or E. (" + Convert.ToString(rs["Type"]) + ")" + Environment.NewLine); 
							result = false; 
							break;
					}
					rs.MoveNext();
					i++;
				};

				if (MissingVenInv || !result)
				{
					MessageBox.Show("There are serious issues with this import file.  See log for details.", Application.ProductName);
					return result;
				}


				UpdateStatus("Checking for duplicate invoices...", 3, 5);
				Application.DoEvents();
				//Now add all the invoices to an array, and check to see if they're duplicated
				i = 0;
				j = 2;
				rs.MoveFirst();
				VendorInvoices = ArraysHelper.InitializeArray<string[, ]>(new int[]{5, 2}, new int[]{0, 0});

				while(!rs.EOF)
				{
					if (Convert.ToString(rs["Type"]) == "A" || Convert.ToString(rs["Type"]) == "T")
					{
						for (int d = 0; d <= i - 1; d++)
						{
							if (VendorInvoices[0, d] == Convert.ToString(rs["VendorCode"]) && VendorInvoices[1, d] == Convert.ToString(rs["InvoiceNbr"]))
							{
								LogFile("Error on line " + j.ToString() + ": The CSV file contains a duplicate invoice: Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
								result = false;
							}
						}
						VendorInvoices[0, i] = Convert.ToString(rs["VendorCode"]);
						VendorInvoices[1, i] = Convert.ToString(rs["InvoiceNbr"]);
						VendorInvoices[2, i] = Convert.ToString(rs["InvoiceAmt"]);
						VendorInvoices[3, i] = Convert.ToString(rs["Type"]);
						i++;
					}
					j++;
					VendorInvoices = ArraysHelper.RedimPreserve<string[, ]>(VendorInvoices, new int[]{5, j + 1});
					rs.MoveNext();
				};
				VchCount = i;

				UpdateStatus("Checking G/L and Expense references...", 4, 5);
				Application.DoEvents();
				//Now check all the G and E rows to verify there is a matching V row
				rs.MoveFirst();
				i = 1;

				while(!rs.EOF)
				{
					if (Convert.ToString(rs["Type"]) == "G" || Convert.ToString(rs["Type"]) == "E")
					{
						VoucherExists = false;
						for (int d = 0; d <= VchCount - 1; d++)
						{
							if (VendorInvoices[0, d] == Convert.ToString(rs["VendorCode"]) && VendorInvoices[1, d] == Convert.ToString(rs["InvoiceNbr"]))
							{
								VoucherExists = true;
							}
						}
						if (!VoucherExists)
						{
							LogFile("Error on line " + (i + 1).ToString() + ": The CSV file contains a G/L or Expense detail record for which there is no corresponding invoice: Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + "." + Environment.NewLine);
							result = false;
						}
					}
					i++;
					rs.MoveNext();
				};




				UpdateStatus("Checking G/L and Expense totals...", 5, 6);
				Application.DoEvents();
				//Now check G/L and Expense Distributions to make sure totals are valid
				for (i = 0; i <= VchCount - 1; i++)
				{
					if (VendorInvoices[1, i] == "A")
					{
						GLDist = 0;
						ExpDist = 0;
						rs.MoveFirst();

						while(!rs.EOF)
						{
							if (VendorInvoices[0, i] == Convert.ToString(rs["VendorCode"]) && VendorInvoices[1, i] == Convert.ToString(rs["InvoiceNbr"]))
							{
								switch(Convert.ToString(rs["Type"]))
								{
									case "G" : 
										double dbNumericTemp7 = 0; 
										if (!Double.TryParse(Convert.ToString(rs["GLAmt"]), NumberStyles.Float , CultureInfo.CurrentCulture.NumberFormat, out dbNumericTemp7))
										{
											LogFile("Error: G/L Distribution record for Vendor " + Convert.ToString(rs["VendorCode"]) + ", invoice " + Convert.ToString(rs["InvoiceNbr"]) + " is missing a value." + Environment.NewLine);
											result = false;
										} 
										GLDist = (decimal) (((double) GLDist) + Convert.ToDouble(rs["GLAmt"])); 
										break;
									case "E" : 
										//UPGRADE_WARNING: (1049) Use of Null/IsNull() detected. More Information: http://www.vbtonet.com/ewis/ewi1049.aspx 
										if (!Convert.IsDBNull(rs["ExpAmount"]))
										{
											ExpDist = (decimal) (((double) ExpDist) + Convert.ToDouble(rs["ExpAmount"]));
										} 
										break;
								}
							}
							rs.MoveNext();
						};
						if (StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) > 0 && StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) < ((double) GLDist))
						{
							LogFile("Error: G/L Distribution total for Vendor " + VendorInvoices[0, i] + ", invoice " + VendorInvoices[1, i] + " exceeds invoice total." + Environment.NewLine + 
							        "Invoice amount is " + VendorInvoices[2, i] + " but detail distribution total is " + GLDist.ToString() + "." + Environment.NewLine);
							result = false;
						}
						if (StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) > 0 && StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) < ((double) ExpDist))
						{
							LogFile("Warning: Expense Distribution total for Vendor " + VendorInvoices[0, i] + ", invoice " + VendorInvoices[1, i] + " exceeds invoice total." + Environment.NewLine + 
							        "Invoice amount is " + VendorInvoices[2, i] + " but detail distribution total is " + GLDist.ToString() + ".  This is not technically an error, but be careful.");
						}
						if (StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) < 0 && StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) > ((double) GLDist))
						{
							LogFile("Error: G/L Distribution total for Vendor " + VendorInvoices[0, i] + ", invoice " + VendorInvoices[1, i] + " exceeds invoice total." + Environment.NewLine + 
							        "Invoice amount is " + VendorInvoices[2, i] + " but detail distribution total is " + GLDist.ToString() + "." + Environment.NewLine);
							result = false;
						}
						if (StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) < 0 && StringsHelper.ToDoubleSafe(VendorInvoices[2, i]) > ((double) ExpDist))
						{
							LogFile("Warning: Expense Distribution total for Vendor " + VendorInvoices[0, i] + ", invoice " + VendorInvoices[1, i] + " exceeds invoice total." + Environment.NewLine + 
							        "Invoice amount is " + VendorInvoices[2, i] + " but detail distribution total is " + GLDist.ToString() + ".  This is not technically an error, but be careful.");
						}
					}
				}
			}
			catch (System.Exception excep)
			{
				MessageBox.Show("Error in procedure RecordSetIsValid:" + Environment.NewLine + excep.Message, Application.ProductName);
			}
			finally
			{
				Cursor = Cursors.Default;
				UpdateStatus("Ready", 1, 1);
			}



			return result;


		}

		private bool FieldExistsInRS(ADORecordSetHelper rs, string fieldName)
		{

			fieldName = fieldName.ToUpper();

			foreach (UpgradeHelpers.DB.FieldHelper fld in rs.Fields)
			{
				if (fld.FieldMetadata.ColumnName.ToUpper() == fieldName)
				{
					return true;
				}
			}

			return false;
		}



		public void UpdateStatus(string Status, int StepNum, int StepOf)
		{
			//Updates Status Bar.  Enter text to display and step number of total completed.
			//Usage:     UpdateStatus "Status text", [Step number], [Total number of steps]
			//   UpdateStatus "Step 1 is underway...", 1, 3
			//   do step 1 stuff
			//   UpdateStatus "Step 2 is underway...", 2, 3
			//   do step 2 stuff
			//   UpdateStatus "Step 2 is complete.", 3, 3

			int Pct = 0;
			double PctLong = 0;

			if (StepOf == 0)
			{
				ProgressBar1.Value = 0;
				txtPercentage.Text = "0";
				txtStatus.Text = Status;
			}
			else
			{
				PctLong = (StepNum / StepOf) * 100;
				Pct = Convert.ToInt32(Math.Round((double) PctLong, 0));
				if ((Pct < 0) || (Pct > 100))
				{
					ProgressBar1.Value = 0;
					txtPercentage.Text = "0";
					txtStatus.Text = Status;
				}
				else
				{
					ProgressBar1.Value = Pct;
					txtPercentage.Text = Pct.ToString();
					txtStatus.Text = Status;
				}
			}
			txtPercentage.Refresh();
			txtStatus.Refresh();
		}
		public string FormatCurrency(string Amount)
		{
			//Formats a currency amount in a user-readable format.
			//Usage:
			//       someCurrency = "1.4"
			//       labelSomeCurrency.Caption = FormatCurrency(someCurrency)
			//  (label displays $1.40)

			if (Amount.StartsWith("-"))
			{
				if (TestRegExp("[-]\\d*.\\d\\d$", Amount) == Amount)
				{
					return "$" + Amount;
				}
				else
				{
					if (TestRegExp("[-]\\d*.\\d$", Amount) == Amount)
					{
						return "$" + Amount + "0";
					}
					else
					{
						return "$" + Amount + ".00";
					}
				}
			}
			else
			{
				if (TestRegExp("\\d*.\\d\\d$", Amount) == Amount)
				{
					return "$" + Amount;
				}
				else
				{
					if (TestRegExp("\\d*.\\d$", Amount) == Amount)
					{
						return "$" + Amount + "0";
					}
					else
					{
						return "$" + Amount + ".00";
					}
				}
			}
		}
		//UPGRADE_NOTE: (7001) The following declaration (DateIsValid) seems to be dead code More Information: http://www.vbtonet.com/ewis/ewi7001.aspx
		//private bool DateIsValid(string dateText)
		//{
			//Tests if an entered date matches the format mm/dd/yyyy.  Returns false if it does not.
			//Usage:  dateString = "12/29/2009"
			//        If DateIsValid(dateString) Then
			//            do some stuff
			//        End If
			//bool result = false;
			//if (TestRegExp("\\d\\d[/]\\d\\d[/]\\d\\d\\d\\d", dateText) == dateText)
			//{
				//result = true;
			//}
			//
			//return result;
		//}
		public string TestRegExp(string myPattern, string myString)
		{
			//Matches regular expression within a string.
			//You need to know regular expressions to use this.  For help,
			//see http://msdn.microsoft.com/en-us/library/ms974570.aspx
			//Usage:
			//       someString = "Microsoft SQL Server 2005 HAL9000 edition"
			//       parsedString = TestRegExp("\s\d*\s", someString)
			//   (parsedString now contains string "2005")

			VBScript_RegExp_55.MatchCollection colMatches = null;
			string RetStr = "";

			VBScript_RegExp_55.RegExp objRegExp = new VBScript_RegExp_55.RegExp();
			objRegExp.Pattern = myPattern;
			objRegExp.IgnoreCase = true;
			objRegExp.Global = true;
			if (objRegExp.Test(myString))
			{
				colMatches = (VBScript_RegExp_55.MatchCollection) objRegExp.Execute(myString); // Execute search.

				foreach (VBScript_RegExp_55.Match objMatch in colMatches)
				{ // Iterate Matches collection.
					RetStr = objMatch.Value;
				}
			}
			else
			{
				RetStr = "String Matching Failed";
			}
			return RetStr;
		}
		public string SearchReplace(ref string sSearch, ref string sFind, string sReplace)
		{
			int lPtr = (sSearch.IndexOf(sFind) + 1);
			while (lPtr > 0)
			{
				sSearch = sSearch.Substring(0, Math.Min(lPtr - 1, sSearch.Length)) + sReplace + sSearch.Substring(Math.Max(sSearch.Length - (Strings.Len(sSearch) - (lPtr + Strings.Len(sFind) - 1)), 0));
				lPtr = Strings.InStr(lPtr + Strings.Len(sReplace), sSearch, sFind, CompareMethod.Binary);
			}
			return sSearch;
		}
		public string GetComputerAndUser()
		{

			int lLength = 255;
			string sWkstaName = new string((char) 0, lLength);
			int lResult = VoucherImportNetSupport.PInvoke.SafeNative.kernel32.GetComputerName(ref sWkstaName, ref lLength);
			sWkstaName = sWkstaName.Substring(0, Math.Min(lLength, sWkstaName.Length));
			lLength = 64;
			string sUserID = new string((char) 0, lLength);
			lResult = VoucherImportNetSupport.PInvoke.SafeNative.mpr.WNetGetUser(0, ref sUserID, ref lLength);
			sUserID = sUserID.Substring(0, Math.Min(sUserID.IndexOf(Strings.Chr(0).ToString()), sUserID.Length));

			return sWkstaName + " / " + sUserID;
		}
		public object WriteLog(string txtComment)
		{
			string SQL = "Insert Into UtilityLog(ULTimeStamp,ULWkStaUser,ULComment) Values('" + DateTimeHelper.ToString(DateTime.Now) + "','" + GetComputerAndUser() + "', '" + txtComment + "')";
			DbCommand TempCommand = null;
			TempCommand = mJUTGlobal.Cn.CreateCommand();
			TempCommand.CommandText = SQL;
			TempCommand.ExecuteNonQuery();
			return null;
		}
		private void FUtil_Closed(Object eventSender, EventArgs eventArgs)
		{
			mInstance.objInstance = null;
			mInstance.objInstances = null;
		}
		//UPGRADE_NOTE: (7001) The following declaration (imgAbout_Click) seems to be dead code More Information: http://www.vbtonet.com/ewis/ewi7001.aspx
		//private void imgAbout_Click()
		//{
			//FSplash.DefInstance.Show();
		//}
		private void DeleteLog()
		{

			string filePathName = Path.GetDirectoryName(Application.ExecutablePath) + "\\VoucherImportLog.txt";
			if (FileSystem.Dir(filePathName + ".ark5", FileAttribute.Normal) != "")
			{
				File.Delete(filePathName + ".ark5");
			}
			if (FileSystem.Dir(filePathName + ".ark4", FileAttribute.Normal) != "")
			{
				File.Copy(filePathName + ".ark4", filePathName + ".ark5");
				File.Delete(filePathName + ".ark4");
			}
			if (FileSystem.Dir(filePathName + ".ark3", FileAttribute.Normal) != "")
			{
				File.Copy(filePathName + ".ark3", filePathName + ".ark4");
				File.Delete(filePathName + ".ark3");
			}
			if (FileSystem.Dir(filePathName + ".ark2", FileAttribute.Normal) != "")
			{
				File.Copy(filePathName + ".ark2", filePathName + ".ark3");
				File.Delete(filePathName + ".ark2");
			}
			if (FileSystem.Dir(filePathName + ".ark1", FileAttribute.Normal) != "")
			{
				File.Copy(filePathName + ".ark1", filePathName + ".ark2");
				File.Delete(filePathName + ".ark1");
			}
			if (FileSystem.Dir(filePathName, FileAttribute.Normal) != "")
			{
				File.Copy(filePathName, filePathName + ".ark1");
				File.Delete(filePathName);
			}

		}
		private void LogFile(string LogLine)
		{


			string filePathName = Path.GetDirectoryName(Application.ExecutablePath) + "\\VoucherImportLog.txt";
			int fileNum = FileSystem.FreeFile();
			FileSystem.FileOpen(fileNum, filePathName, OpenMode.Append, OpenAccess.Default, OpenShare.Default, -1);
			FileSystem.PrintLine(fileNum, LogLine);
			FileSystem.FileClose(fileNum);
		}
		private void CreateDelimSchema(string Path, string FILENAME)
		{
			//Creates a schema.ini file in the bank statement folder with the correct parsing information for VB's ADO functionality


			if (mJUTGlobal.PathExists(Path + "schema.ini"))
			{
				File.Delete(Path + "schema.ini");
			}

			int fileNum = FileSystem.FreeFile();
			string filePath = Path + "schema.ini";
			FileSystem.FileOpen(fileNum, filePath, OpenMode.Output, OpenAccess.Default, OpenShare.Default, -1);

			FileSystem.PrintLine(fileNum, "[" + FILENAME + "]");
			FileSystem.PrintLine(fileNum, "Format=CSVDelimited");
			FileSystem.PrintLine(fileNum, "Maxrows=0");
			FileSystem.PrintLine(fileNum, "ColNameHeader=True");
			FileSystem.PrintLine(fileNum, "Col1=Type Text");
			FileSystem.PrintLine(fileNum, "Col2=VoucherDate Text");
			FileSystem.PrintLine(fileNum, "Col3=VendorCode Text");
			FileSystem.PrintLine(fileNum, "Col4=PONbr Text");
			FileSystem.PrintLine(fileNum, "Col5=InvoiceNbr Text");
			FileSystem.PrintLine(fileNum, "Col6=InvoiceDate Text");
			FileSystem.PrintLine(fileNum, "Col7=DueDate Text");
			FileSystem.PrintLine(fileNum, "Col8=DiscountDate Text");
			FileSystem.PrintLine(fileNum, "Col9=InvoiceAmt Text");
			FileSystem.PrintLine(fileNum, "Col10=NonDiscAmt Text");
			FileSystem.PrintLine(fileNum, "Col11=VchReference Text");
			FileSystem.PrintLine(fileNum, "Col12=SeparateCheck Text");
			FileSystem.PrintLine(fileNum, "Col13=APAcct Text");
			FileSystem.PrintLine(fileNum, "Col14=GLDistAcct Text");
			FileSystem.PrintLine(fileNum, "Col15=GLAmt Text");
			FileSystem.PrintLine(fileNum, "Col16=TrustBank Text");
			FileSystem.PrintLine(fileNum, "Col17=ExpClient Text");
			FileSystem.PrintLine(fileNum, "Col18=ExpMatter Text");
			FileSystem.PrintLine(fileNum, "Col19=ExpCode Text");
			FileSystem.PrintLine(fileNum, "Col20=ExpTaskCode Text");
			FileSystem.PrintLine(fileNum, "Col21=ExpUnits Text");
			FileSystem.PrintLine(fileNum, "Col22=ExpAmount Text");
			FileSystem.PrintLine(fileNum, "Col23=ExpNarrative Text");
			FileSystem.PrintLine(fileNum, "Col24=ExpBillNote Text");

			FileSystem.FileClose(fileNum);

		}
		private void GetFieldLengths()
		{

			string SQL = "SELECT     FldMatter.FldMatter, FldClient.FldClient" + Environment.NewLine + 
			             "FROM         (SELECT     SpTxtValue AS FldMatter" + Environment.NewLine + 
			             "                       FROM          SysParam" + Environment.NewLine + 
			             "                       WHERE      (SpName = 'FldMatter')) AS FldMatter CROSS JOIN" + Environment.NewLine + 
			             "                          (SELECT     SpTxtValue AS FldClient" + Environment.NewLine + 
			             "                            FROM          SysParam AS SysParam_1" + Environment.NewLine + 
			             "                            WHERE      (SpName = 'FldClient')) AS FldClient";
			mJUTGlobal.Cmd.CommandText = SQL;
			ADORecordSetHelper rsFL = ADORecordSetHelper.Open(mJUTGlobal.Cmd, "");
			fldMat = Convert.ToInt32(Double.Parse(TestRegExp("\\d+", Convert.ToString(rsFL["FldMatter"]))));
			fldCli = Convert.ToInt32(Double.Parse(TestRegExp("\\d+", Convert.ToString(rsFL["FldClient"]))));
		}
	}
}