using System;
using System.IO;
using System.Data;
using System.Data.OleDb;
using System.Data.Common;
using System.Data.SqlClient;
using System.Text;
using System.Windows.Forms;
using Gizmox.CSharp;
using Gizmox.Data;
using JurisAuthenticator;
using JurisUtilityBase.Properties;

namespace JurisUtilityBase
{
    public class JurisUtility
    {
        #region Private members/properties
        private string SqlConnectionStringFormat
        {
            get { return Resources.SqlConnectionString; }
        }

        private readonly SqlConnection[] _connections = new SqlConnection[3];
        private readonly SqlCommand[] _commands = new SqlCommand[3];
        private Instance _instance;
        private readonly Instances _instances;

        #endregion

        #region Public properites

        public Instances Companies
        {
            get { return _instances; }
        }

        public Instance Company { get { return _instance; } }

        public string GetLastBackupDateQuery
        {
            get { return Resources.GetLastBackupDate; }
        }

        public bool DbOpen { get; set; }

        #endregion

        #region Constructor

        public JurisUtility()
        {
            DbOpen = false;
            _instances = new Instances();
        }
        #endregion

        #region Public methods

        public void CloseDatabase()
        {
            for (int i = 0; i < _connections.Length; ++i)
            {
                if (_connections[i] != null && _connections[i].State != ConnectionState.Closed)
                {
                    _connections[i].Close();
                    _connections[i] = null;
                }
            }
            DbOpen = false;
        }

        public bool OpenDatabase()
        {
            string[] connectionTypes = {"Juris", "Juris", "master"};
            try
            {
                for (int i = 0; i < _connections.Length; ++i)
                {
                    _connections[i] = new SqlConnection
                    {
                        ConnectionString = GetConnectionString(connectionTypes[i]),
                    };
                    _connections[i].Open();
                    _connections[i].CommandTimeout(0);
                    _commands[i] = new SqlCommand
                    {
                        CommandTimeout = 0, 
                        CommandType = CommandType.Text,
                        Connection = _connections[i]
                    };
                }
                DbOpen = true;

            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, @"Could not open database", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                DbOpen = false;
            }
            return DbOpen;
        }

        public Instance SetInstance(string companyCode)
        {
            _instance = Companies.GetItem(companyCode);
            return _instance;
        }

        public void BeginTransaction(int connection)
        {
            var transaction = _connections[connection].BeginTransaction();
            _commands[connection].Transaction = transaction;
        }

        public void CommitTransaction(int connection)
        {
            _connections[connection].CommitTransaction();
            _commands[connection].Transaction.Commit();
        }

        public DataSet ExecuteSql(int connection, string sql)
        {
            return _connections[connection].Execute(sql);
        }

        public int ExecuteNonQuery(int connection, string sql)
        {
            return _connections[connection].ExecuteNonQuery(sql);
        }

        public DataSet RecordsetFromSQL(string sql)
        {
            DataSet ds = ExecuteSqlCommand(0, sql);
            return ds;
        }
        public DataSet RecordSetFromCSV(string FileName)
        {
            OleDbConnection conn = new OleDbConnection
                   ("Provider=Microsoft.Jet.OleDb.4.0; Data Source = " + 
                     Path.GetDirectoryName(FileName) + 
                     "; Extended Properties = \"Text;HDR=YES;FMT=Delimited\"");

            conn.Open();

            OleDbDataAdapter adapter = new OleDbDataAdapter
                   ("SELECT * FROM " + Path.GetFileName(FileName), conn);

            DataSet ds = new DataSet("Temp");
            adapter.Fill(ds);

            conn.Close();
            return ds;
        }

        static public DataSet ConvertToRecordset(DataTable inTable)
        {
            DataSet ds = new DataSet("Recordset");
            ds.Tables.Add(inTable);
            return ds;
        }

 
        public DataSet ExecuteSqlCommand(int command, string sql)
        {
            DataSet ds = null;
            _commands[command].CommandText = sql;
            using (var da = new SqlDataAdapter())
            {
                da.SelectCommand = _commands[command];
                ds = new DataSet();
                da.Fill(ds);
            }
            return ds;
        }

        public int ExecuteNonQueryCommand(int command, string sql)
        {
            try
            {
                _commands[command].CommandText = sql;
                return _commands[command].ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Clipboard.SetText(sql);
                MessageBox.Show(ex.Message);
            }
            return 0;
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Encrtyp/decrypt a string
        /// </summary>
        /// <param name="secret">the string you wish to encrypt or decrypt</param>
        /// <param name="password">the password with which to encrypt the string</param>
        /// <returns></returns>
        private string Encrypt(string secret, string password)
        {
            int len = 0;
            int x = 0;

            string temp = secret;
            len = password.Length;
            for (x = 1; x <= temp.Length; x++)
            {
                int ch = Strings.Asc(Strings.Mid(password, Convert.ToInt32((x % len) - len * Conversion.BoolToInt(((x % len) == 0))), 1));
                Strings.MidStmtStr(ref temp, x, 1, Strings.Chr(Strings.Asc(Strings.Mid(temp, x, 1)) ^ ch).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return temp;
        }

        private string GetConnectionString(string connectionType)
        {
            var ascii = Encoding.ASCII;
            byte[] id1Bytes = {0x16,0x15,0x1b,0x04,0x0c,0x1a,0x13,0x6e,0x55,0x47,0x53,0x51,0x51,0x14};
            //byte[] id2Bytes = {0x1d,0x14,0x01,0x08,0x11,0x3b,0x71,0x03,0x53};
            //byte[] id3Bytes = {0x1d,0x14,0x01,0x08,0x11,0x5c,0x19,0x60,0x55,0x44};
            byte[] id5Bytes = {0x16, 0x15, 0x1B, 0x04, 0x0c, 0x1a, 0x65, 0x15, 0x2e};
            var id1 = ascii.GetString(id1Bytes);
            //var id2 = ascii.GetString(id2Bytes);
            //var id3 = ascii.GetString(id3Bytes);
            const string id4 = "Wasabi!";
            var id5 = ascii.GetString(id5Bytes);

            var connectionString = SqlConnectionStringFormat.Replace("#SERVER#", Company.ServerName);
            switch (connectionType)
            {
                case "Juris":
                    connectionString = connectionString.Replace("#DATABASE#", Company.DatabaseName);
                    break;
                case "JBills":
                    connectionString = connectionString.Replace("#DATABASE#", Company.DatabaseName.Replace("Juris", "JBills"));
                    break;
                case "master":
                    connectionString = connectionString.Replace("#DATABASE#", "master");
                    break;
            }
            connectionString = connectionString.Replace("#USERID#", Encrypt(id5, id4));
            connectionString = connectionString.Replace("#DATA#", Encrypt(id1, id4));
            return connectionString;
        }

        #endregion
    }
}
