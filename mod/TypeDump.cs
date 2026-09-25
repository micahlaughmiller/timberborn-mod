using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// One-time discovery aid. GET /dump writes every Timberborn type (and the
    /// members of the ones currently alive in the scene) to a text file, so the
    /// real names can be pasted into GameAccess.CandidateNames.
    /// </summary>
    internal static class TypeDump
    {
        public static string Dump()
        {
            var path = Path.Combine(Application.persistentDataPath, "timberborn-ai-types.txt");
            var sb = new StringBuilder();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.IndexOf("Timberborn", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            sb.AppendLine($"=== {assemblies.Length} Timberborn assemblies ===");

            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                sb.AppendLine().AppendLine($"--- {asm.GetName().Name} ({types.Length} types) ---");
                foreach (var t in types.OrderBy(t => t.FullName))
                    sb.AppendLine(t.FullName);
            }

            sb.AppendLine().AppendLine("=== members of live MonoBehaviours ===");
            var live = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .Select(m => m.GetType())
                .Where(t => t.FullName != null &&
                            t.FullName.IndexOf("Timberborn", StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct()
                .OrderBy(t => t.FullName);

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var t in live)
            {
                sb.AppendLine().AppendLine(t.FullName);
                foreach (var p in t.GetProperties(flags)) sb.AppendLine($"    prop {p.PropertyType.Name} {p.Name}");
                foreach (var f in t.GetFields(flags))     sb.AppendLine($"    field {f.FieldType.Name} {f.Name}");
                foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName))
                    sb.AppendLine($"    method {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name))})");
            }

            File.WriteAllText(path, sb.ToString());
            return $"{{\"written\":{Json.Str(path)}}}";
        }
    }
}
