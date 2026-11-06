using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;

namespace Jps.Core.Data
{
    /// <summary>
    /// An open connection to one Juris company database. Replaces JurisUtility from the old utility base;
    /// every tool gets the same session from the shell instead of opening its own.
    /// </summary>
    public sealed class JurisSession : IDisposable
    {
        private const string ConnectionFormat = "Server=#SERVER#;Database=#DATABASE#;UID=#USERID#;PWD=#DATA#;Trusted_Connection=NO";

        private readonly SqlConnection _connection;

        public CompanyInfo Company { get; }
        public string JurisDbName => Company.DatabaseName;
        public string JBillsDbName => "JBills" + Company.Code;
        public string FirmName { get; private set; } = "";

        private JurisSession(CompanyInfo company, SqlConnection connection)
        {
            Company = company;
            _connection = connection;
        }

        public static JurisSession Open(CompanyInfo company)
        {
            var connection = new SqlConnection(BuildConnectionString(company, company.DatabaseName));
            connection.Open();
            var session = new JurisSession(company, connection);
            session.FirmName = Convert.ToString(session.Scalar(
                "SELECT sysparam.sptxtvalue FROM sysparam WHERE sysparam.spname = 'FirmName'")) ?? "";
            return session;
        }

        /// <summary>A new, unopened connection to another database on the same server (e.g. JBills or master).</summary>
        public SqlConnection CreateConnection(string database) => new SqlConnection(BuildConnectionString(Company, database));

        // One connection is shared by the shell and every tool, and tools run their work on a background thread.
        // The gate serialises access; it is re-entrant, so code inside InTransaction can call Query/Execute freely.
        private readonly object _gate = new object();
        private SqlTransaction _transaction;

        public DataTable Query(string sql, params SqlParameter[] parameters)
        {
            lock (_gate)
            using (var command = CreateCommand(sql, parameters))
            using (var adapter = new SqlDataAdapter(command))
            {
                var table = new DataTable();
                adapter.Fill(table);
                return table;
            }
        }

        /// <summary>
        /// Runs SQL that returns no rows. WARNING: the returned number also counts rows written by Juris's triggers
        /// (audit _Log tables, DeletedRows...), so never use it to decide how many rows the statement itself changed —
        /// use <see cref="ExecuteRows"/> for that.
        /// </summary>
        public int Execute(string sql, params SqlParameter[] parameters)
        {
            lock (_gate)
            using (var command = CreateCommand(sql, parameters))
                return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Runs ONE insert/update/delete/merge and returns how many rows that statement itself changed (its @@ROWCOUNT),
        /// not counting rows Juris's triggers write. Use this for "exactly one row" checks and for counts shown to users.
        /// The statement must not return a result set (no OUTPUT without INTO).
        /// </summary>
        public int ExecuteRows(string dml, params SqlParameter[] parameters)
        {
            lock (_gate)
            using (var command = CreateCommand(dml.TrimEnd().TrimEnd(';') + ";\r\nselect @@rowcount;", parameters))
                return Convert.ToInt32(command.ExecuteScalar());
        }

        public object Scalar(string sql, params SqlParameter[] parameters)
        {
            lock (_gate)
            using (var command = CreateCommand(sql, parameters))
            {
                var value = command.ExecuteScalar();
                return value == DBNull.Value ? null : value;
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> in one SQL transaction: everything commits, or nothing does.
        /// Query/Execute/Scalar called inside it (on this thread) join the transaction automatically.
        /// </summary>
        public void InTransaction(Action work)
        {
            lock (_gate)
            {
                if (_transaction != null)
                {
                    work();
                    return;
                }
                _transaction = _connection.BeginTransaction();
                try
                {
                    work();
                    _transaction.Commit();
                }
                catch
                {
                    try { _transaction.Rollback(); } catch (InvalidOperationException) { /* already rolled back by SQL Server */ }
                    throw;
                }
                finally
                {
                    _transaction.Dispose();
                    _transaction = null;
                }
            }
        }

        public T InTransaction<T>(Func<T> work)
        {
            var result = default(T);
            InTransaction(() => { result = work(); });
            return result;
        }

        public SqlCommand CreateCommand(string sql, params SqlParameter[] parameters)
        {
            var command = new SqlCommand(sql, _connection, _transaction) { CommandTimeout = 0 };
            if (parameters != null)
                command.Parameters.AddRange(parameters);
            return command;
        }

        public DateTime? GetLastBackupDate()
        {
            const string sql = @"declare @vDate datetime
select @vDate = max(backup_finish_date) from msdb.dbo.backupset where database_name = @db and backup_finish_date <= getdate()
if @vDate is null select @vDate = crdate from master.dbo.sysdatabases where name = @db
select @vDate";
            var value = Scalar(sql, new SqlParameter("@db", JurisDbName));
            return value == null ? (DateTime?)null : Convert.ToDateTime(value);
        }

        /// <summary>Writes to Juris's UtilityLog table, as every JPS utility has always done.</summary>
        public void WriteUtilityLog(string comment)
        {
            Execute("insert into UtilityLog (ULTimeStamp, ULWkStaUser, ULComment) values (getdate(), cast(@user as varchar(100)), cast(@comment as varchar(8000)))",
                new SqlParameter("@user", Environment.MachineName + "/" + Environment.UserDomainName + "\\" + Environment.UserName),
                new SqlParameter("@comment", comment));
        }

        public void Dispose() => _connection.Dispose();

        private static string BuildConnectionString(CompanyInfo company, string database)
        {
            // Same credentials and obfuscation as JurisUtility.GetConnectionString in the old utility base.
            var ascii = Encoding.ASCII;
            byte[] id1Bytes = { 0x16, 0x15, 0x1b, 0x04, 0x0c, 0x1a, 0x13, 0x6e, 0x55, 0x47, 0x53, 0x51, 0x51, 0x14 };
            byte[] id5Bytes = { 0x16, 0x15, 0x1B, 0x04, 0x0c, 0x1a, 0x65, 0x15, 0x2e };
            const string id4 = "Wasabi!";

            return ConnectionFormat
                .Replace("#SERVER#", company.ServerName)
                .Replace("#DATABASE#", database)
                .Replace("#USERID#", Xor(ascii.GetString(id5Bytes), id4))
                .Replace("#DATA#", Xor(ascii.GetString(id1Bytes), id4));
        }

        private static string Xor(string secret, string password)
        {
            var chars = secret.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                chars[i] = (char)(chars[i] ^ password[i % password.Length]);
            return new string(chars);
        }
    }
}
