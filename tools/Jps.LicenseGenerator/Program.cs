using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Jps.Core.Catalog;
using Jps.Core.Licensing;

namespace Jps.LicenseGenerator
{
    internal static class Program
    {
        private static readonly string DefaultKeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "JPS Licensing", "jps-license-private.xml");

        private const string Usage = @"JpsLicense - issues signed JPS Tool Library licenses (internal use only)

  JpsLicense modules
      Lists every tool id that can go in a license.

  JpsLicense keygen [--key <private.xml>] --public <public.xml>
      Creates the license key pair ONCE. Keep the private key safe and backed up;
      put the public key in src\Jps.Core\Licensing\license-public-key.xml.

  JpsLicense sign --firm ""Smith and Lane LLP"" --firmkey ""<SysParam FirmName>"" --db Juris1 [--db Juris2]
                  --module JPS.MATCLOSER [--module ...] [--id LIC-2026-0142] [--key <private.xml>] --out <JPS.license>
      --module all   includes every tool.

  JpsLicense verify <JPS.license>
      Checks a license against the public key built into the app.

Default private key: " + "%USERPROFILE%\\JPS Licensing\\jps-license-private.xml";

        private static int Main(string[] args)
        {
            try
            {
                switch (args.FirstOrDefault()?.ToLowerInvariant())
                {
                    case "modules": return ListModules();
                    case "keygen": return KeyGen(Options(args));
                    case "sign": return Sign(Options(args));
                    case "verify" when args.Length == 2: return VerifyFile(args[1]);
                    default:
                        Console.WriteLine(Usage);
                        return 1;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is CryptographicException)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 2;
            }
        }

        private static int ListModules()
        {
            foreach (var group in ToolCatalog.All.GroupBy(t => t.Group))
            {
                Console.WriteLine(group.Key);
                foreach (var tool in group)
                    Console.WriteLine("  {0,-18} {1}", tool.Id, tool.Name);
            }
            return 0;
        }

        private static int KeyGen(ILookup<string, string> options)
        {
            var keyPath = options["--key"].FirstOrDefault() ?? DefaultKeyPath;
            var publicPath = options["--public"].FirstOrDefault() ?? throw new ArgumentException("--public is required.");
            if (File.Exists(keyPath))
                throw new ArgumentException("A private key already exists at " + keyPath + ". Refusing to overwrite it: every license issued with it would stop working.");

            using (var rsa = new RSACryptoServiceProvider(3072))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath)));
                File.WriteAllText(keyPath, rsa.ToXmlString(includePrivateParameters: true));
                File.WriteAllText(publicPath, rsa.ToXmlString(includePrivateParameters: false));
            }
            Console.WriteLine("Private key: " + Path.GetFullPath(keyPath) + "  (back this up; never ship it)");
            Console.WriteLine("Public key:  " + Path.GetFullPath(publicPath));
            return 0;
        }

        private static int Sign(ILookup<string, string> options)
        {
            var modules = options["--module"].ToList();
            if (modules.Any(m => m.Equals("all", StringComparison.OrdinalIgnoreCase)))
                modules = ToolCatalog.All.Select(t => t.Id).ToList();
            var unknown = modules.Where(m => ToolCatalog.Find(m.ToUpperInvariant()) == null).ToList();
            if (unknown.Count > 0)
                throw new ArgumentException("Unknown tool id(s): " + string.Join(", ", unknown) + ". Run 'JpsLicense modules'.");

            var license = new LicenseInfo
            {
                LicenseId = options["--id"].FirstOrDefault() ?? "LIC-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                Firm = Required(options, "--firm"),
                FirmKey = Required(options, "--firmkey"),
                Databases = options["--db"].ToList(),
                Issued = DateTime.Today.ToString("yyyy-MM-dd"),
                Modules = modules.Select(m => m.ToUpperInvariant()).Distinct().OrderBy(m => m).ToList(),
            };
            if (license.Databases.Count == 0) throw new ArgumentException("At least one --db is required.");
            if (license.Modules.Count == 0) throw new ArgumentException("At least one --module is required.");

            var keyPath = options["--key"].FirstOrDefault() ?? DefaultKeyPath;
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.FromXmlString(File.ReadAllText(keyPath));
                license.Signature = Convert.ToBase64String(rsa.SignData(license.SignedPayload(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }

            var outPath = options["--out"].FirstOrDefault() ?? LicenseInfo.FileName;
            license.Write(outPath);
            Console.WriteLine("Wrote {0} for {1}: {2} tool(s).", Path.GetFullPath(outPath), license.Firm, license.Modules.Count);
            return VerifyFile(outPath);
        }

        private static int VerifyFile(string path)
        {
            if (LicenseService.TryVerify(path, out var license, out var problem))
            {
                Console.WriteLine("Valid. {0} | databases: {1} | tools: {2}", license.Firm, string.Join(", ", license.Databases), string.Join(", ", license.Modules));
                return 0;
            }
            Console.WriteLine("NOT valid: " + problem);
            return 3;
        }

        private static string Required(ILookup<string, string> options, string name) =>
            options[name].FirstOrDefault() ?? throw new ArgumentException(name + " is required.");

        private static ILookup<string, string> Options(string[] args)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--"))
                    throw new ArgumentException("Unexpected argument: " + args[i]);
                if (i + 1 >= args.Length)
                    throw new ArgumentException(args[i] + " needs a value.");
                pairs.Add(new KeyValuePair<string, string>(args[i].ToLowerInvariant(), args[++i]));
            }
            return pairs.ToLookup(p => p.Key, p => p.Value);
        }
    }
}
