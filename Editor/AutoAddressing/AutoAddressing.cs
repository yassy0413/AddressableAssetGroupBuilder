using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace AddressableAssetGroupBuilder
{
    /// <summary>
    /// Settings and cache for auto addressing: the per-user on/off switch, the active batch,
    /// and the guards that decide whether the postprocessor may run at all.
    /// </summary>
    public static class AutoAddressing
    {
        public const string MenuPath = "Tools/Addressable Group Builder/Auto Addressing";
        private const string EnabledPrefKey = "AddressableAssetGroupBuilder.AutoAddressing.Enabled";
        private const string MaxAssetsPrefKey = "AddressableAssetGroupBuilder.AutoAddressing.MaxAssetsPerUpdate";

        private static AddressableAssetGroupBuilderBatch cachedBatch;
        private static string[] cachedDefinitionPaths = Array.Empty<string>();
        private static bool cacheValid;
        private static bool hadBatch;

        /// <summary>Per-user switch (EditorPrefs). Default on.</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        /// <summary>Maximum changed and removed paths processed in one editor update.</summary>
        public static int MaxAssetsPerUpdate
        {
            get => Math.Max(1, EditorPrefs.GetInt(MaxAssetsPrefKey, 500));
            set => EditorPrefs.SetInt(MaxAssetsPrefKey, Math.Max(1, value));
        }

        /// <summary>
        /// The single batch with autoAddressing enabled, or null (none, or more than one).
        /// </summary>
        public static AddressableAssetGroupBuilderBatch ActiveBatch
        {
            get
            {
                // hadBatch: the cached object may have been unloaded (null) since the last refresh
                if (!cacheValid || (hadBatch && cachedBatch == null))
                {
                    Refresh();
                }

                return cachedBatch;
            }
        }

        /// <summary>True when queued changes can be applied. Temporary pauses do not discard the queue.</summary>
        public static bool IsAvailable =>
            Enabled
            && !Application.isBatchMode
            && !AssetDatabase.IsAssetImportWorkerProcess()
            && !EditorApplication.isCompiling
            && !EditorApplication.isUpdating
            && !EditorApplication.isPlayingOrWillChangePlaymode
            && !AddressableAssetGroupBuilder.IsBuilding
            && !AutoAddressingProcessor.IsApplying
            && AddressableAssetSettingsDefaultObject.SettingsExists;

        /// <summary>Directory that holds the addressable settings and group assets (our own output; never an input).</summary>
        public static string SettingsDirectory
        {
            get
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    return null;
                }

                var path = AssetDatabase.GetAssetPath(settings);
                return string.IsNullOrEmpty(path)
                    ? null
                    : (Path.GetDirectoryName(path) ?? string.Empty).Replace('\\', '/');
            }
        }

        public static void InvalidateCache()
        {
            cacheValid = false;
        }

        /// <summary>
        /// True when the path is a builder / batch definition asset (by cached path, or by main asset type for new files).
        /// </summary>
        public static bool IsDefinitionAsset(string path)
        {
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (cachedDefinitionPaths.Contains(path))
            {
                return true;
            }

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type == typeof(AddressableAssetGroupBuilderBatch) || type == typeof(AddressableAssetGroupBuilder);
        }

        /// <summary>
        /// True when a changed path is worth resolving: a real asset under Assets/ or Packages/,
        /// not our own output and not a definition asset.
        /// </summary>
        public static bool IsCandidate(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (!path.StartsWith("Assets/", StringComparison.Ordinal) &&
                !path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }

            var settingsDirectory = SettingsDirectory;
            if (!string.IsNullOrEmpty(settingsDirectory) &&
                path.StartsWith(settingsDirectory + "/", StringComparison.Ordinal))
            {
                return false;
            }

            if (IsDefinitionAsset(path))
            {
                return false;
            }

            return true;
        }

        private static void Refresh()
        {
            cacheValid = true;
            cachedBatch = null;

            var batches = AssetDatabase.FindAssets($"t:{nameof(AddressableAssetGroupBuilderBatch)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<AddressableAssetGroupBuilderBatch>)
                .Where(static x => x != null)
                .ToArray();

            var enabled = batches.Where(static x => x.autoAddressing).ToArray();
            if (enabled.Length > 1)
            {
                Debug.LogError(
                    "Auto Addressing is enabled on more than one batch; it stays disabled until exactly one remains: " +
                    string.Join(", ", enabled.Select(static x => x.name)),
                    enabled[0]);
            }
            else if (enabled.Length == 1)
            {
                cachedBatch = enabled[0];
            }

            hadBatch = cachedBatch != null;

            cachedDefinitionPaths = batches
                .Select(AssetDatabase.GetAssetPath)
                .Concat(batches.SelectMany(static x => x.ValidBuilders).Select(AssetDatabase.GetAssetPath))
                .Distinct()
                .ToArray();
        }

        [MenuItem(MenuPath, priority = 1000)]
        private static void ToggleEnabled()
        {
            Enabled = !Enabled;
            Debug.Log($"Auto Addressing: {(Enabled ? "enabled" : "disabled")} (per user)");
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}