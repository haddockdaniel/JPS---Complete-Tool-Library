using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Jps.Core.Catalog
{
    /// <summary>One sellable tool. Id is what a license lists, so never change an Id once shipped.</summary>
    [DataContract]
    public sealed class ToolDefinition
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "group")] public string Group { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "icon")] public string Icon { get; set; }
        [DataMember(Name = "template", IsRequired = false)] public string Template { get; set; }

        public bool HasTemplate => !string.IsNullOrEmpty(Template);
    }

    public static class ToolCatalog
    {
        /// <summary>Display order of the groups in the shell.</summary>
        public static readonly string[] GroupOrder = { "Client Matter", "Bulk Operations", "A/P", "Banks", "Batches", "Special" };

        private static IReadOnlyList<ToolDefinition> _tools;

        public static IReadOnlyList<ToolDefinition> All => _tools ?? (_tools = Load());

        public static ToolDefinition Find(string id) => All.FirstOrDefault(t => t.Id == id);

        private static IReadOnlyList<ToolDefinition> Load()
        {
            using (var stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("Jps.Catalog.tools.json"))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ToolDefinition>));
                return (List<ToolDefinition>)serializer.ReadObject(stream);
            }
        }
    }
}
