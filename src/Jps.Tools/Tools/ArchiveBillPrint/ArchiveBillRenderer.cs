using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Jps.Core.Data;

namespace Jps.Tools.Tools.ArchiveBillPrint
{
    /// <summary>Turns one archived Juris invoice into a PDF file.</summary>
    internal interface IArchiveBillRenderer
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
        public static IArchiveBillRenderer Create(JurisSession session) => new JurisArchiveRenderer(session.Company.Code);
    }

    /// <summary>
    /// Renders exactly as the original tool did, through Juris's own server library (JurisSVR21) and the ActiveReports 6
    /// runtime that the original shipped: PrintPrebill.PreviewArchiveByBillNumber, then the stored PDF for PDF-type archives,
    /// otherwise the rebuilt ActiveReports report exported with PdfExport (PDF 1.3, image quality 100).
    /// The ActiveReports runtime license is the embedded "JPS Tool Library.exe.licenses" resource in Jps.Shell.
    /// Call every member from the same STA thread.
    /// </summary>
    internal sealed class JurisArchiveRenderer : IArchiveBillRenderer
    {
        private const int ArchiveTypePdf = 2;            // JEnumerations.JurisBillArchiveType.jbatpdf
        private readonly string _companyCode;
        private bool _open;

        public JurisArchiveRenderer(string companyCode)
        {
            _companyCode = companyCode;
        }

        public bool IsAvailable =>
            File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ActiveReports6.dll"))
            && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JurisSVR21.dll"));

        public string UnavailableReason =>
            "the Juris report files (ActiveReports6.dll, JurisSVR21.dll) are missing from the JPS Tool Library folder. Reinstall the tool library.";

        public void Open()
        {
            if (_open) return;
            try
            {
                LogOn();
            }
            catch (Exception ex)
            {
                throw new ArchiveRenderException("There was an error logging into your books. Check your license. (" + ex.Message + ")", ex);
            }
            _open = true;
        }

        // Kept out of Open() so a missing Juris assembly surfaces as an exception Open() can describe.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogOn()
        {
            var server = new JurisSVR.JurisEntryPoint().JurisServer;
            if (server == null)
                throw new InvalidOperationException("the Juris server object couldn't be created");
            server.Companies.CurrentCompany = "Company" + _companyCode;
            server.DataConnections.Datamode = (JDataEngine.JDataBase.enmDatamode)0;
            server.OpenDatabase(string.Empty, false);
        }

        public void RenderToFile(int billNumber, string path)
        {
            if (!_open) Open();
            var prebill = new JurisSVR.PrintPrebill();
            try
            {
                var result = (int)prebill.PreviewArchiveByBillNumber(billNumber, false);
                if (result != 0)
                    throw new ArchiveRenderException("Juris couldn't load its archive (result code " + result + ")");

                if ((int)prebill.ArchiveType == ArchiveTypePdf)
                {
                    var image = prebill.ArchiveImage;
                    if (string.IsNullOrEmpty(image))
                        throw new ArchiveRenderException("the archive image is empty");
                    // The archive holds the PDF bytes as an ANSI string (the original wrote it with VB FilePut, i.e. ANSI bytes).
                    File.WriteAllBytes(path, Encoding.Default.GetBytes(image));
                }
                else
                {
                    var report = prebill.ReportObject;
                    if (report == null)
                        throw new ArchiveRenderException("Juris didn't return a report for the archive");
                    report.Run(false);
                    var pdf = new DataDynamics.ActiveReports.Export.Pdf.PdfExport
                    {
                        Version = DataDynamics.ActiveReports.Export.Pdf.PdfVersion.Pdf13,
                        ImageQuality = (DataDynamics.ActiveReports.Export.Pdf.ImageQuality)100,
                    };
                    try
                    {
                        pdf.Export(report.Document, path);
                    }
                    finally
                    {
                        pdf.Dispose();
                    }
                }
            }
            finally
            {
                (prebill as IDisposable)?.Dispose();
            }
        }
    }
}
