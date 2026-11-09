using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Jps.Core.Data;

namespace Jps.Tools.Tools.ArchiveBillPrint
{
    /// <summary>Turns one archived Juris invoice into a PDF file.</summary>
    internal interface IArchiveBillRenderer : IDisposable
    {
        /// <summary>False when this install can't render archived bills; <see cref="UnavailableReason"/> says why.</summary>
        bool IsAvailable { get; }
        string UnavailableReason { get; }

        /// <summary>Logs on to the Juris company once, before the first invoice. Throws <see cref="ArchiveRenderException"/> if it can't.</summary>
        void Open();

        /// <summary>Writes the invoice's archive image to <paramref name="path"/> as a PDF. Throws on any failure.</summary>
        void RenderToFile(int billNumber, string path);
    }

    internal sealed class ArchiveRenderException : Exception
    {
        public ArchiveRenderException(string message, Exception inner = null) : base(message, inner) { }
    }

    internal static class ArchiveBillRenderer
    {
        public static IArchiveBillRenderer Create(JurisSession session) => new HelperProcessRenderer(session.Company.Code);
    }

    /// <summary>
    /// Renders through "JPS Bill Renderer.exe", a separate process that calls Juris's own JurisWrapper.dll (4.32 set, backwards
    /// compatible) exactly as the original Archive Bill Bulk Print Tool did: logon to the company, then GetBillImage per invoice
    /// (stored PDF for PDF archives, otherwise the ActiveReports report exported to PDF). The helper carries its own
    /// ActiveReports runtime license. Running it out of process keeps the memory-heavy rendering, and any crash in it, out of
    /// the Tool Library.
    /// </summary>
    internal sealed class HelperProcessRenderer : IArchiveBillRenderer
    {
        private const string HelperName = "JPS Bill Renderer.exe";
        private static readonly TimeSpan PerInvoiceTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan LogonTimeout = TimeSpan.FromMinutes(2);

        // Juris/ActiveReports leak memory over long runs; a fresh helper gets all of it back. Recycle after this many invoices,
        // or sooner once the helper's memory passes the limit (it is a 32-bit process, so it can't grow much past 2 GB).
        private const int RecycleAfterInvoices = 100;
        private const long RecycleAboveBytes = 1L * 1024 * 1024 * 1024;

        private readonly string _companyCode;
        private Process _process;
        private int _renderedByThisHelper;

        public HelperProcessRenderer(string companyCode)
        {
            _companyCode = companyCode;
        }

        private static string HelperPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HelperName);

        public bool IsAvailable =>
            File.Exists(HelperPath) && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JurisWrapper.dll"));

        public string UnavailableReason =>
            "the Juris bill printing files (" + HelperName + ", JurisWrapper.dll) are missing from the JPS Tool Library folder. Reinstall the tool library.";

        public void Open()
        {
            if (_process != null && !_process.HasExited) return;
            var start = new ProcessStartInfo(HelperPath, "--company \"" + _companyCode + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            };
            _process = Process.Start(start);
            var first = ReadLine(LogonTimeout) ?? "LOGON-FAILED\tThe bill printing helper stopped before it could log on.";
            if (first != "READY")
            {
                var why = first.StartsWith("LOGON-FAILED\t") ? first.Substring(13) : first;
                Dispose();
                throw new ArchiveRenderException("There was an error logging into your books. Check your license. (" + why + ")");
            }
        }

        public void RenderToFile(int billNumber, string path)
        {
            if (_process != null && !_process.HasExited && NeedsRecycle())
                Dispose();
            if (_process == null || _process.HasExited)
            {
                Open();
                _renderedByThisHelper = 0;
            }
            _renderedByThisHelper++;
            _process.StandardInput.WriteLine(billNumber + "\t" + path);
            _process.StandardInput.Flush();
            var answer = ReadLine(PerInvoiceTimeout);
            if (answer == null)
            {
                Dispose(); // hung or crashed: start a fresh helper for the next invoice
                throw new ArchiveRenderException("Juris stopped responding while printing this invoice.");
            }
            if (answer != "OK")
                throw new ArchiveRenderException(answer.StartsWith("FAIL\t") ? answer.Substring(5) : answer);
        }

        private bool NeedsRecycle()
        {
            if (_renderedByThisHelper >= RecycleAfterInvoices) return true;
            try
            {
                _process.Refresh();
                return _process.PrivateMemorySize64 > RecycleAboveBytes;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        /// <summary>One line from the helper, or null if it exits or doesn't answer in time.</summary>
        private string ReadLine(TimeSpan timeout)
        {
            var read = Task.Run(() => _process.StandardOutput.ReadLine());
            return read.Wait(timeout) ? read.Result : null;
        }

        public void Dispose()
        {
            if (_process == null) return;
            try
            {
                if (!_process.HasExited)
                {
                    try { _process.StandardInput.WriteLine(); _process.StandardInput.Flush(); } catch (IOException) { }
                    if (!_process.WaitForExit(5000)) _process.Kill();
                }
            }
            catch (InvalidOperationException) { /* already gone */ }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }
}
