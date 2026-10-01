using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Udon.Editor;
using VRC.Udon.Graph;

namespace UdonExpressionDriver.Editor
{
    /// <summary>
    /// Dumps the full Udon node-definition whitelist (the set UdonSharp's binder checks) to JSON.
    /// Run once after UdonSharp/SDK updates: Tools > Udon Expression Driver > Dump Udon Exposure.
    /// </summary>
    public static class UdonExposureDumper
    {
        private const string MenuPath = "Tools/Udon Expression Driver/Dump Udon Exposure";

        /// <summary>Menu item: writes the Udon node-definition whitelist to a JSON file.</summary>
        [MenuItem(MenuPath)]
        public static void Dump()
        {
            // same UdonEditorInterface setup as CompilerUdonInterface.CacheInit
            // (UdonBehaviourTypeResolver included), so the set matches what the
            // UdonSharp binder accepts
            var fullNames = UdonEditorManager.Instance.GetNodeDefinitions()
                .Select(d => d.fullName)
                .Distinct()
                .OrderBy(s => s, System.StringComparer.Ordinal)
                .ToList();

            // signature format: <SanitizedTypeFullName>.__<MemberName>__<Arg1>_<Arg2>...__<ReturnType>
            var payload = new
            {
                format = "udon-class-exposure",
                generated = System.DateTime.Now.ToString("s"),
                count = fullNames.Count,
                definitions = fullNames
            };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented);

            var path = EditorUtility.SaveFilePanel(
                "Save Udon Exposure Dump", "", "udon-class-exposure.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            File.WriteAllText(path, json);
            Debug.Log($"[UED] Wrote {fullNames.Count} Udon node definitions to {path}");
        }
    }
}
