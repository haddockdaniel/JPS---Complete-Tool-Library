using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Jps.Core.Modules
{
    /// <summary>
    /// Finds IJurisTool implementations: the built-in tools assembly (Jps.Tools) first, then any extra
    /// Modules\*.dll next to the exe (a hotfix module there overrides the built-in tool with the same id).
    /// </summary>
    public static class ModuleLoader
    {
        public static IReadOnlyDictionary<string, IJurisTool> LoadAll(params Assembly[] builtIn)
        {
            var tools = new Dictionary<string, IJurisTool>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in builtIn)
                AddFrom(assembly, tools);

            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Modules");
            if (!Directory.Exists(folder))
                return tools;

            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                try
                {
                    AddFrom(Assembly.LoadFrom(file), tools);
                }
                catch (Exception ex) when (ex is BadImageFormatException || ex is FileLoadException || ex is ReflectionTypeLoadException || ex is TypeLoadException)
                {
                    Trace.TraceWarning("Skipped module {0}: {1}", file, ex.Message);
                }
            }
            return tools;
        }

        private static void AddFrom(Assembly assembly, Dictionary<string, IJurisTool> tools)
        {
            var types = assembly.GetExportedTypes()
                .Where(t => typeof(IJurisTool).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null);
            foreach (var type in types)
            {
                var tool = (IJurisTool)Activator.CreateInstance(type);
                tools[tool.Id] = tool;
            }
        }
    }
}
