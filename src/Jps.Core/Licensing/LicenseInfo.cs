using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Jps.Core.Licensing
{
    /// <summary>
    /// A firm's license file (JSON). Perpetual: a module listed here is owned forever.
    /// The signature covers <see cref="SignedPayload"/>, so editing any field invalidates the file.
    /// </summary>
    [DataContract]
    public sealed class LicenseInfo
    {
        public const string FileName = "JPS.license";

        [DataMember(Name = "licenseId", Order = 1)] public string LicenseId { get; set; }
        [DataMember(Name = "firm", Order = 2)] public string Firm { get; set; }
        /// <summary>Must match SysParam 'FirmName' of the connected database (case and outer spaces ignored).</summary>
        [DataMember(Name = "firmKey", Order = 3)] public string FirmKey { get; set; }
        [DataMember(Name = "databases", Order = 4)] public List<string> Databases { get; set; } = new List<string>();
        [DataMember(Name = "issued", Order = 5)] public string Issued { get; set; }
        [DataMember(Name = "modules", Order = 6)] public List<string> Modules { get; set; } = new List<string>();
        [DataMember(Name = "signature", Order = 7)] public string Signature { get; set; }

        /// <summary>The exact bytes that get signed. Independent of JSON formatting and field order.</summary>
        public byte[] SignedPayload()
        {
            var lines = new[]
            {
                "JPS-LICENSE-V1",
                LicenseId ?? "",
                Firm ?? "",
                NormalizeFirmKey(FirmKey),
                string.Join(",", (Databases ?? new List<string>()).Select(d => d.Trim().ToUpperInvariant()).OrderBy(d => d, StringComparer.Ordinal)),
                Issued ?? "",
                string.Join(",", (Modules ?? new List<string>()).Select(m => m.Trim().ToUpperInvariant()).OrderBy(m => m, StringComparer.Ordinal)),
            };
            return Encoding.UTF8.GetBytes(string.Join("\n", lines));
        }

        public static string NormalizeFirmKey(string value) => (value ?? "").Trim().ToUpperInvariant();

        public static LicenseInfo Read(string path)
        {
            // ReadAllText drops a byte-order mark, which the JSON serializer rejects (Notepad adds one on save).
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                return (LicenseInfo)new DataContractJsonSerializer(typeof(LicenseInfo)).ReadObject(stream);
        }

        public void Write(string path)
        {
            using (var stream = File.Create(path))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, ownsStream: false, indent: true))
                new DataContractJsonSerializer(typeof(LicenseInfo)).WriteObject(writer, this);
        }
    }
}
