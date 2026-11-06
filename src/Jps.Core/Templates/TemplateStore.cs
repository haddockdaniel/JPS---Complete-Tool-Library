using System.IO;
using Jps.Core.Catalog;

namespace Jps.Core.Templates
{
    /// <summary>Import templates are embedded in this assembly so they can be saved with no internet access.</summary>
    public static class TemplateStore
    {
        public static void Save(ToolDefinition tool, string destinationPath)
        {
            using (var source = typeof(TemplateStore).Assembly.GetManifestResourceStream("Jps.Templates." + tool.Template))
            {
                if (source == null)
                    throw new FileNotFoundException("The template for " + tool.Name + " is missing from this build.", tool.Template);
                using (var target = File.Create(destinationPath))
                    source.CopyTo(target);
            }
        }

        public static string FileFilter(ToolDefinition tool) =>
            Path.GetExtension(tool.Template).ToLowerInvariant() == ".csv"
                ? "CSV file (*.csv)|*.csv"
                : "Excel workbook (*.xlsx)|*.xlsx";
    }
}
