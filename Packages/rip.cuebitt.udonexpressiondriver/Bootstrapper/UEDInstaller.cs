using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using VRC.PackageManagement.Core;

namespace UdonExpressionDriver.Bootstrapper
{
    /// <summary>First-import installer: downloads the VRC Avatars SDK and writes the stripped VRCSDK3A.dll the editor code compiles against.</summary>
    [InitializeOnLoad]
    public static class UEDInstaller
    {
        private const string AssemblyGuid = "67cc4cb7839cd3741b63733d5adf0442";

        static UEDInstaller()
        {
            // kicked off at load, callers await Installed instead of blocking the editor
            Installed = Install();
        }

        /// <summary>Completes with true when UED's stripped SDK assembly is present or was installed.</summary>
        public static Task Installed { get; private set; }

        private static async Task<bool> Install()
        {
            var packageName = GetPackageNameForType(typeof(UEDInstaller));
            if (CheckForExisting(packageName))
            {
                Debug.Log("[UdonExpressionDriver] Udon Expression Driver is installed.");
                return true;
            }

            Debug.Log("[UdonExpressionDriver] Installing Udon Expression Driver...");

            var avatarsPackageUrl = Repos.Official.GetPackage("com.vrchat.avatars").Url;
            var tempFolderPath = Path.Combine(Path.GetTempPath(), "UED_Temp"); // Store in a system temp folder
            Directory.CreateDirectory(tempFolderPath);

            Debug.Log("[UdonExpressionDriver] Downloading VRC Avatars SDK package...");
            var success = await DownloadAndExtract(avatarsPackageUrl, tempFolderPath);
            if (!success)
            {
                Debug.LogError("[UdonExpressionDriver] Failed to download or extract avatars package.");
                return false;
            }

            // the archive name doubles as the name of the folder it unpacks into
            var downloadedPackageDirectory =
                Path.Combine(tempFolderPath, Path.GetFileNameWithoutExtension(avatarsPackageUrl));
            var avatarsDllPath =
                Path.Combine(downloadedPackageDirectory, "Runtime/VRCSDK/Plugins/VRCSDK3A.dll");

            // nothing to strip if the SDK ever moves this file
            if (!File.Exists(avatarsDllPath))
            {
                Debug.LogError($"[Udon Expression Driver] Could not find VRCSDK3A.dll at {avatarsDllPath}");
                return false;
            }

            Debug.Log("[UdonExpressionDriver] Processing downloaded assembly...");
            // into the package so the editor asmdef can reference it
            var outputAssemblyPath =
                Path.GetFullPath($"Packages/{packageName}/Editor/VRCSDK/Plugins/VRCSDK3A.dll");
            StripAssembly(packageName, avatarsDllPath, outputAssemblyPath);

            Debug.Log("[UdonExpressionDriver] Importing downloaded assets...");
            // sync import so ChangeGuid has a meta to rewrite, which pins the guid
            AssetDatabase.ImportAsset(outputAssemblyPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh();
            GuidChanger.ChangeGuid(outputAssemblyPath, AssemblyGuid);

            Debug.Log("[UdonExpressionDriver] Finished installing Udon Expression Driver!");
            return true;
        }

        private static bool CheckForExisting(string packageName)
        {
            // either the full SDK is installed, or our own stripped copy
            var possibleDllPaths = new[]
            {
                Path.GetFullPath("Packages/com.vrchat.avatars/Runtime/VRCSDK/Plugins/VRCSDK3A.dll"),
                Path.GetFullPath($"Packages/{packageName}/Editor/VRCSDK/Plugins/VRCSDK3A.dll")
            };

            return File.Exists(possibleDllPaths[0]) || File.Exists(possibleDllPaths[1]);
        }

        private static async Task<bool> DownloadAndExtract(string url, string destination)
        {
            var filename = Path.GetFileName(url);
            var tempZip = Path.Combine(destination, filename);

            var extractDirName = Path.GetFileNameWithoutExtension(tempZip);
            var extractPath = Path.Combine(destination, extractDirName);

            using (var req = UnityWebRequest.Get(url))
            {
                // stream the zip straight to disk instead of buffering it in memory
                req.downloadHandler = new DownloadHandlerFile(tempZip);
                var resp = req.SendWebRequest();


                // UnityWebRequest isn't awaitable here, so poll it
                while (!resp.isDone) await Task.Delay(100);

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[UdonExpressionDriver] Failed to download avatars package: status: {req.result}");
                    return false;
                }

                Debug.Log("[UdonExpressionDriver] Extracting avatars package...");

                try
                {
                    var finalExtractPath = extractPath;

                    // unzipping is blocking IO, keep it off the editor's main thread
                    await Task.Run(() =>
                    {
                        // clear a half-extracted folder left by an earlier failed run
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);

                        using (var archive = ZipFile.OpenRead(tempZip))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                var fullPath = Path.Combine(finalExtractPath, entry.FullName);
                                // zip directory entries have an empty Name, file entries don't
                                if (string.IsNullOrEmpty(entry.Name))
                                {
                                    Directory.CreateDirectory(fullPath);
                                }
                                else
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                                    entry.ExtractToFile(fullPath, true);
                                }
                            }
                        }
                    });
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UdonExpressionDriver] Extraction failed: {e.Message}");
                    return false;
                }
                finally
                {
                    // temp zip goes whether or not the extract worked
                    if (File.Exists(tempZip))
                        File.Delete(tempZip);
                }
            }

            return true;
        }

        private static void StripAssembly(string packageName, string inputPath, string outputPath)
        {
            // the exact slice of VRCSDK3A.dll the editor code touches, committed
            // to the package; regenerate from git history (old BFS stripper) if
            // the avatars SDK version changes
            var retainListPath =
                Path.GetFullPath($"Packages/{packageName}/Bootstrapper/VRCSDK3A.retain.txt");

            // the Plugins folder doesn't exist yet on a fresh install
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            AssemblyStripper.StripExcept(inputPath, retainListPath, outputPath);
        }

        private static string GetPackageNameForType(Type type)
        {
            // no API answers "which package is this in", so read it off the script path
            var script = AssetDatabase.FindAssets("t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<MonoScript>)
                .FirstOrDefault(s => s != null && s.GetClass() == type);

            if (script == null)
            {
                return null;
            }

            var assetPath = AssetDatabase.GetAssetPath(script);

            if (assetPath.StartsWith("Packages/"))
            {
                // e.g. "Packages/com.unity.textmeshpro/Scripts/TextMeshPro.cs"
                var parts = assetPath.Split('/');
                if (parts.Length > 1)
                {
                    var packageName = parts[1];
                    return packageName;
                }
            }

            return "Assets";
        }
    }
}