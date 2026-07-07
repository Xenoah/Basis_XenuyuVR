#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// CLI entry point for building the Windows client, mirroring what
/// BasisHeadlessBuild does for servers:
///   Unity.exe -batchmode -quit -projectPath Basis
///             -executeMethod SakiikaBatchBuild.BuildWindows
/// </summary>
public static class SakiikaBatchBuild
{
    public static void BuildWindows()
    {
        try
        {
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new Exception("Addressable settings not found.");
            }

            ForcePackedModeDataBuilder(settings);
            Debug.Log("[SakiikaBatchBuild] Building Addressables...");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult addrResult);
            if (!string.IsNullOrEmpty(addrResult.Error))
            {
                throw new Exception($"Addressables build failed: {addrResult.Error}");
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new Exception("No enabled scenes in EditorBuildSettings.");
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "../Builds/Windows/さきいかVR.exe",
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            };

            Debug.Log($"[SakiikaBatchBuild] Building player with {scenes.Length} scene(s)...");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            Debug.Log($"[SakiikaBatchBuild] Result={summary.result} Errors={summary.totalErrors} Warnings={summary.totalWarnings} Size={summary.totalSize} Output={summary.outputPath}");

            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SakiikaBatchBuild] FAILED: {ex}");
            EditorApplication.Exit(1);
        }
    }

    private static void ForcePackedModeDataBuilder(AddressableAssetSettings settings)
    {
        for (int index = 0; index < settings.DataBuilders.Count; index++)
        {
            if (settings.GetDataBuilder(index) is BuildScriptPackedMode)
            {
                settings.ActivePlayerDataBuilderIndex = index;
                return;
            }
        }

        throw new Exception("Addressables BuildScriptPackedMode data builder was not found.");
    }
}
#endif
