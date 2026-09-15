using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AddressableAssetGroupBuilder
{
    [CreateAssetMenu(
        fileName = "AddressableAssetGroupBuilderBatch_",
        menuName = "Addressables/Group Builder Batch")]
    public sealed class AddressableAssetGroupBuilderBatch : ScriptableObject
    {
        public string defaultGroupName = "DefaultGroup";
        public AddressableAssetGroupTemplate defaultGroupTemplate;
        public AddressableAssetGroupBuilder[] builders = Array.Empty<AddressableAssetGroupBuilder>();

        [Tooltip("If true, remove unused groups when build.")]
        public bool removeUnusedGroupsWhenBuild = true;

        [HideInInspector]
        public string[] keepGroupNamesRegexPattern = new[] { "Localization-.*" };

        [Tooltip(
            "If true, imported / moved / deleted assets are addressed automatically by AssetPostprocessor using this batch. Only one batch in the project may enable this.")]
        public bool autoAddressing = false;

        public IEnumerable<AddressableAssetGroupBuilder> ValidBuilders => builders.Where(static x => x != null);

        public bool IsKeepGroup(string groupName) =>
            keepGroupNamesRegexPattern != null && keepGroupNamesRegexPattern.Any(x => Regex.IsMatch(groupName, x));

        /// <summary>
        /// Verify group templates across all builders. Logs errors and returns false when inconsistent.
        /// </summary>
        public bool VerifyGroups()
        {
            var verifier = new AddressableAssetGroupBuilder.GroupVerifier();
            foreach (var builder in ValidBuilders)
            {
                verifier.Join(builder);
            }

            return verifier.Verify();
        }

        /// <summary>
        /// Resolve a single asset path against every builder, in order. The last match wins,
        /// which is the same precedence the full build has.
        /// </summary>
        public bool TryResolve(string path, string guid, AddressableAssetGroupBuilder.ResolveContext context,
            out AddressableAssetGroupBuilder.Resolution result)
        {
            result = default;
            var found = false;
            foreach (var builder in ValidBuilders)
            {
                if (builder.TryResolve(path, guid, context, out var candidate))
                {
                    result = candidate;
                    found = true;
                }
            }

            return found;
        }

        public void TestAll()
        {
            var count = ValidBuilders.Sum(static x => x.Test());
            Debug.Log($"{count} asset entries had found at {name}");
        }

        public void BuildAll()
        {
            var stopwatch = Stopwatch.StartNew();
            var work = new AddressableAssetGroupBuilder.Work();
            AddressableAssetGroupBuilder.IsBuilding = true;
            try
            {
                var addressableAssetSettings = AddressableAssetSettingsDefaultObject.Settings;

                AddressableAssetGroupBuilder.RemoveMissingGroupReferences();

                if (!ApplyDefaultGroup(addressableAssetSettings))
                {
                    return;
                }

                // build groups
                foreach (var builder in ValidBuilders)
                {
                    builder.Build(work);
                }

                // remove unused groups
                if (removeUnusedGroupsWhenBuild)
                {
                    foreach (var group in addressableAssetSettings.groups.ToArray())
                    {
                        if (group == null || group.Default || work.groupMap.ContainsKey(group.Name))
                        {
                            continue;
                        }

                        if (IsKeepGroup(group.Name))
                        {
                            continue;
                        }

                        addressableAssetSettings.RemoveGroup(group);
                    }
                }

                AddressableAssetGroupBuilder.Finalize(work, keepGroupNamesRegexPattern);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                AddressableAssetGroupBuilder.IsBuilding = false;
                EditorUtility.ClearProgressBar();
                Debug.Log($"AddressablesGroupBuilderBatch finished. {stopwatch.Elapsed.TotalSeconds:F3} seconds");
            }
        }

        public bool ApplyDefaultGroup(AddressableAssetSettings addressableAssetSettings)
        {
            if (string.IsNullOrEmpty(defaultGroupName))
            {
                return true;
            }

            if (defaultGroupTemplate == null)
            {
                Debug.LogError("Default Group Template must be set.", this);
                return false;
            }

            var group = addressableAssetSettings.FindGroup(defaultGroupName);
            if (group == null)
            {
                group = addressableAssetSettings.CreateGroup(
                    defaultGroupName, true, false, false, null, defaultGroupTemplate.GetTypes());
            }

            defaultGroupTemplate.ApplyToAddressableAssetGroup(group);
            return true;
        }
    }
}