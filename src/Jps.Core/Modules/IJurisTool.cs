using System.Windows;
using Jps.Core.Catalog;
using Jps.Core.Data;

namespace Jps.Core.Modules
{
    /// <summary>
    /// Implemented once per tool, in Jps.Tools (or, for a hotfix, in an assembly in the Modules folder).
    /// The shell only calls <see cref="CreateView"/> when the tool is licensed and a company is connected.
    /// </summary>
    public interface IJurisTool
    {
        /// <summary>Must equal the tool's id in Catalog\tools.json (e.g. "JPS.MATCLOSER").</summary>
        string Id { get; }

        FrameworkElement CreateView(IToolContext context);
    }

    /// <summary>What the shell hands every tool: the shared connection and shared services.</summary>
    public interface IToolContext
    {
        JurisSession Session { get; }
        ToolDefinition Definition { get; }

        /// <summary>Prompts the user to save this tool's embedded template. No-op if the tool has none.</summary>
        void SaveTemplate();
    }
}
