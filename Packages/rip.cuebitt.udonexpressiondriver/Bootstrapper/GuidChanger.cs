using System.IO;
using UnityEditor;
using UnityEngine;

namespace UdonExpressionDriver.Bootstrapper
{
    /// <summary>Rewrites the GUID inside an asset's .meta file, in place.</summary>
    public static class GuidChanger
    {
        /// <summary>Changes the GUID of an asset at path "assetPath".</summary>
        public static void ChangeGuid(string assetPath, string newGuid)
        {
            var metaPath = assetPath + ".meta";

            // no meta on disk means nothing references this dll yet
            if (!File.Exists(metaPath))
            {
                Debug.LogError("[Udon Expression Driver] Meta file not found: " + metaPath);
                return;
            }

            // rewrite in place, the rest of the meta must survive untouched
            var lines = File.ReadAllLines(metaPath);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("guid: "))
                {
                    lines[i] = "guid: " + newGuid;
                    break;
                }

            File.WriteAllLines(metaPath, lines);

            // refresh so Unity re-reads the meta and re-resolves references to it
            AssetDatabase.Refresh();
        }
    }
}