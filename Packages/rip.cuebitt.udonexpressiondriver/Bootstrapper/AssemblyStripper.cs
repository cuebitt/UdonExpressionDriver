using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace UdonExpressionDriver.Bootstrapper
{
    public static class AssemblyStripper
    {
        /// <summary>
        ///     Strip a .dll of every type and member not in the retain list.
        ///     The retain list (full names of types and members) is generated
        ///     from a full VRCSDK3A.dll and committed to the package; types
        ///     are matched by exact FullName, members contain "::".
        /// </summary>
        /// <param name="retainListPath">Path to the retain list file</param>
        public static void StripExcept(string inputPath, string retainListPath, string outputPath)
        {
            var keepTypes = new HashSet<string>(StringComparer.Ordinal);
            var keepMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(retainListPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("::")) keepMembers.Add(line.Trim());
                else keepTypes.Add(line.Trim());
            }

            // in-memory read leaves the downloaded dll on disk untouched
            var readerParams = new ReaderParameters { ReadSymbols = false, InMemory = true };
            var asm = AssemblyDefinition.ReadAssembly(inputPath, readerParams);
            var module = asm.MainModule;

            // snapshot first, pruning mutates the module's own type collection
            var allTypes = new List<TypeDefinition>();
            foreach (var t in module.Types)
                FlattenNested(t, allTypes);

            foreach (var t in allTypes)
            {
                if (!keepTypes.Contains(t.FullName))
                {
                    // nested types hang off their parent, not the module list
                    if (t.IsNested)
                        t.DeclaringType.NestedTypes.Remove(t);
                    else
                        module.Types.Remove(t);
                    continue;
                }

                t.Methods.RemoveWhere(m => !keepMembers.Contains(m.FullName));
                t.Fields.RemoveWhere(f => !keepMembers.Contains(f.FullName));
                t.Properties.RemoveWhere(p => !keepMembers.Contains(p.FullName));
                t.Events.RemoveWhere(e => !keepMembers.Contains(e.FullName));
            }

            // Final save (no symbols written -> PDB removed)
            var writerParams = new WriterParameters { WriteSymbols = false };
            asm.Write(outputPath, writerParams);
        }

        private static void FlattenNested(TypeDefinition td, List<TypeDefinition> into)
        {
            into.Add(td);
            foreach (var nested in td.NestedTypes)
                FlattenNested(nested, into);
        }
    }

    internal static class CecilExtensions
    {
        public static void RemoveWhere<T>(this ICollection<T> collection, Func<T, bool> predicate)
        {
            // can't mutate a collection while enumerating it, snapshot the matches
            var toRemove = collection.Where(predicate).ToList();
            foreach (var item in toRemove)
                collection.Remove(item);
        }
    }
}
