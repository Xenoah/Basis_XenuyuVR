#if UNITY_EDITOR
using HVR.LicenseReview;
using HVR.LicenseReview.Editor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tracks the SakiikaVR-specific third-party packages in the license manifest
/// and re-bakes it, so the in-game "Third-Party Licenses" tab lists them.
/// Runs from the menu or via CLI:
///   Unity.exe -batchmode -quit -projectPath Basis
///             -executeMethod SakiikaLicenseBake.Bake
/// </summary>
public static class SakiikaLicenseBake
{
    private const string ManifestPath = "Packages/dev.hai-vr.hvr.license-review/BasisFrameworkLicenseManifest.asset";
    private const string UrlPrefix = "https://github.com/SakiikaVR/SakiikaVR/tree/developer/Basis/Packages/";

    [MenuItem("Sakiika/Bake Third-Party Licenses")]
    public static void Bake()
    {
        var manifest = AssetDatabase.LoadAssetAtPath<LicenseManifest>(ManifestPath);
        if (manifest == null)
        {
            throw new System.Exception($"License manifest not found at {ManifestPath}");
        }

        TrackWithSpdx(manifest, "jp.lilxyzw.basispatcher", "LICENSE", "MIT");
        TrackWithSpdx(manifest, "jp.lilxyzw.facecamera", "LICENSE", "MIT");
        TrackWithSpdx(manifest, "jp.lilxyzw.emock", "LICENSE", "MIT");

        // The fork ships packages that don't exist upstream, so point license
        // links at this repository instead of BasisVR/Basis.
        manifest.urlPrefix = UrlPrefix;

        LicenseBaker.BakeManifest(manifest, tuple => $"{manifest.urlPrefix}{tuple.Item1}/{tuple.Item2}");

        EditorUtility.SetDirty(manifest);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SakiikaLicenseBake] Baked {manifest.baked.Length} license entries.");
    }

    private static void TrackWithSpdx(LicenseManifest manifest, string packageName, string path, string spdx)
    {
        if (!manifest.IsTracked(packageName, path))
        {
            manifest.Track(packageName, new[] { path });
        }

        // Track() doesn't fill in the SPDX id, which drives the license name
        // shown in the settings menu — set it on the entry we just tracked.
        foreach (var trackedPackage in manifest.trackedPackages)
        {
            if (trackedPackage.packageName != packageName) continue;
            var licenses = trackedPackage.licenses;
            for (int i = 0; i < licenses.Length; i++)
            {
                if (licenses[i].path == path && string.IsNullOrWhiteSpace(licenses[i].spdx))
                {
                    licenses[i].spdx = spdx;
                }
            }
        }
    }
}
#endif
