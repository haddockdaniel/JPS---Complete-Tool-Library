using System.Collections.Generic;
using System.Linq;
using JurisAuthenticator;

namespace Jps.Core.Data
{
    /// <summary>A Juris company as registered on this workstation (wraps JurisAuthenticator.Instance).</summary>
    public sealed class CompanyInfo
    {
        public string Code { get; }
        public string Display { get; }
        public string DatabaseName { get; }
        public string ServerName { get; }
        public bool IsDefault { get; }

        private CompanyInfo(Instance instance)
        {
            Code = instance.Code;
            Display = instance.Key;
            DatabaseName = instance.DatabaseName;
            ServerName = instance.ServerName;
            IsDefault = instance.Default == Instance.JurisDefaultCompany.jdcJuris;
        }

        public static IReadOnlyList<CompanyInfo> LoadAll()
        {
            var instances = new Instances();
            return instances.Cast<object>().Cast<Instance>().Select(i => new CompanyInfo(i)).ToList();
        }

        public override string ToString() => Display;
    }
}
