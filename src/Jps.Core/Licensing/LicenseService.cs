using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Jps.Core.Data;

namespace Jps.Core.Licensing
{
    public enum ToolAccess
    {
        /// <summary>Listed in a valid license, and the license matches the connected firm (or nothing is connected yet).</summary>
        Licensed,
        /// <summary>Not listed in the license, or there is no valid license.</summary>
        NotLicensed,
        /// <summary>Listed, but the license belongs to a different firm or database than the one connected.</summary>
        WrongFirm,
    }

    /// <summary>Loads and verifies the license file and answers "can this tool run here?".</summary>
    public sealed class LicenseService
    {
        /// <summary>Where an imported license is stored. Checked before the copy installed next to the exe.</summary>
        public static string ImportedLicensePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JPS", "Tool Library", LicenseInfo.FileName);

        public static string InstalledLicensePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LicenseInfo.FileName);

        public LicenseInfo License { get; private set; }
        public string LoadedFrom { get; private set; }
        /// <summary>Why no license is active, in words a user can act on. Null when a license is active.</summary>
        public string Problem { get; private set; }
        public bool IsValid => License != null;

        public void Load()
        {
            License = null;
            LoadedFrom = null;
            Problem = "No license file is installed. All tools are locked.";

            foreach (var path in new[] { ImportedLicensePath, InstalledLicensePath })
            {
                if (!File.Exists(path))
                    continue;
                if (TryVerify(path, out var license, out var problem))
                {
                    License = license;
                    LoadedFrom = path;
                    Problem = null;
                    return;
                }
                Problem = problem;
            }
        }

        public ToolAccess AccessFor(string moduleId, JurisSession session)
        {
            if (License == null || !License.Modules.Any(m => string.Equals(m.Trim(), moduleId, StringComparison.OrdinalIgnoreCase)))
                return ToolAccess.NotLicensed;
            if (session != null && !MatchesFirm(session))
                return ToolAccess.WrongFirm;
            return ToolAccess.Licensed;
        }

        public bool MatchesFirm(JurisSession session) =>
            License != null
            && License.Databases.Any(d => string.Equals(d.Trim(), session.JurisDbName, StringComparison.OrdinalIgnoreCase))
            && LicenseInfo.NormalizeFirmKey(License.FirmKey) == LicenseInfo.NormalizeFirmKey(session.FirmName);

        /// <summary>Verifies a license file and, if good, copies it to <see cref="ImportedLicensePath"/>.</summary>
        public bool Import(string sourcePath, out string problem)
        {
            if (!TryVerify(sourcePath, out _, out problem))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(ImportedLicensePath));
            File.Copy(sourcePath, ImportedLicensePath, overwrite: true);
            Load();
            return true;
        }

        public static bool TryVerify(string path, out LicenseInfo license, out string problem)
        {
            license = null;
            try
            {
                var candidate = LicenseInfo.Read(path);
                if (string.IsNullOrEmpty(candidate.Signature) || !Verify(candidate))
                {
                    problem = "The license file has been changed or was not issued by JPS.";
                    return false;
                }
                license = candidate;
                problem = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Runtime.Serialization.SerializationException || ex is FormatException)
            {
                problem = "The license file could not be read: " + ex.Message;
                return false;
            }
        }

        private static bool Verify(LicenseInfo license)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(PublicKeyXml());
                return rsa.VerifyData(license.SignedPayload(), Convert.FromBase64String(license.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }

        private static string PublicKeyXml()
        {
            using (var stream = typeof(LicenseService).Assembly.GetManifestResourceStream("Jps.Licensing.public-key.xml"))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
    }
}
