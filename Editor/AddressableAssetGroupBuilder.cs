using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace AddressableAssets.GroupBuilder
{
    [CreateAssetMenu(
        fileName = "AddressableAssetGroupBuilder_",
        menuName = "Addressables/Group Builder")]
    public sealed class AddressableAssetGroupBuilder : ScriptableObject
    {
        [Tooltip("Path matching is case-insensitive")]
        public bool caseInsensitivePathPatterns = false;
        [Tooltip("Exclude paths matching any of the regular expressions")]
        public string[] ignorePathPatterns = Array.Empty<string>();
        [Tooltip("Contains paths matching any of the regular expressions (if not empty)")]
        public string[] includePathPatterns = Array.Empty<string>();
        public Group[] groups = Array.Empty<Group>();

        [Serializable]
        public sealed class Group
        {
            public string name;
            public string label;
            public AddressableAssetGroupTemplate template;
            [Tooltip("Required scripting define symbol like UNITY_EDITOR")]
            public string symbol;

            public DefaultAsset[] searchInFolders = Array.Empty<DefaultAsset>();
            [Tooltip("Filter for FindAssets")]
            public string filter;
            [Tooltip("Add regular expression path matching. The resulting variables can be used in label.")]
            public string pattern;

            public string Label(string path) =>
                (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(pattern)) ?
                    label : Regex.Replace(path, pattern, label);
        }

        public sealed class Work
        {
            public Dictionary<string, AddressableAssetGroup> groupMap = new();
            public HashSet<string> labels = new();
            public HashSet<string> guids = new();
        }

        public void Test()
        {
            foreach (var groupPolicy in groups.Where(VerifySymbol))
            {
                foreach (var asset in FindAssetsQuery(groupPolicy).OrderBy(x => x.path))
                {
                    Debug.Log($"asset: {asset.path}\nlabel: {asset.label}");
                }
            }
        }

        public void Build()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var work = new Work();
            try
            {
                RemoveMissingGroupReferences();
                Build(work);
                Finalize(work);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Debug.Log($"AddressablesGroupBuilder finished. {stopwatch.Elapsed.TotalSeconds:F3} seconds");
            }
        }

        public void Build(Work work)
        {
            var addressableAssetSettings = AddressableAssetSettingsDefaultObject.Settings;

            foreach (var groupPolicy in groups.Where(VerifySymbol))
            {
                var title = $"[{name}][{groupPolicy.name}]";

                // create group
                if (!work.groupMap.TryGetValue(groupPolicy.name, out var group))
                {
                    group = addressableAssetSettings.FindGroup(groupPolicy.name);
                    if (group == null)
                    {
                        group = addressableAssetSettings.CreateGroup(
                            groupPolicy.name, false, true, false, null, groupPolicy.template.GetTypes());
                    }
                    groupPolicy.template.ApplyToAddressableAssetGroup(group);
                    work.groupMap.Add(groupPolicy.name, group);
                }

                // create entry
                var assets = FindAssetsQuery(groupPolicy).ToArray();
                var assetLength = assets.Length;
                for (var index = 0; index < assetLength; ++index)
                {
                    var (guid, path, label) = assets[index];
                    EditorUtility.DisplayProgressBar(title, path, (index + 1) / (float)assetLength);

                    var entry = addressableAssetSettings.FindAssetEntry(guid);
                    if (entry == null || entry.parentGroup != group)
                    {
                        entry = addressableAssetSettings.CreateOrMoveEntry(guid, group, true, false);
                    }
                    work.guids.Add(guid);

                    if (!string.IsNullOrEmpty(label))
                    {
                        addressableAssetSettings.AddLabel(label, false);
                        entry.labels.Add(label);
                        work.labels.Add(label);
                    }
                }
            }
        }

        public static void Finalize(Work work)
        {
            var addressableAssetSettings = AddressableAssetSettingsDefaultObject.Settings;

            EditorUtility.DisplayProgressBar("Finalize", "", 1f);

            // remove unused entries
            var entries = new List<AddressableAssetEntry>();
            addressableAssetSettings.GetAllAssets(entries, false);
            foreach (var entry in entries.Where(x => !work.guids.Contains(x.guid)))
            {
                if (!work.groupMap.ContainsKey(entry.parentGroup.Name))
                {
                    work.groupMap.Add(entry.parentGroup.Name, entry.parentGroup);
                }
                entry.parentGroup.RemoveAssetEntry(entry, false);
            }

            // remove unused labels
            foreach (var label in addressableAssetSettings.GetLabels().Where(x => !work.labels.Contains(x)))
            {
                addressableAssetSettings.RemoveLabel(label, false);
            }

            // apply modified groups
            foreach (var group in work.groupMap)
            {
                group.Value.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            }

            // apply settings
            addressableAssetSettings
                .SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);

            AssetDatabase.SaveAssets();
        }

        public static void RemoveMissingGroupReferences()
        {
            var addressableAssetSettings = AddressableAssetSettingsDefaultObject.Settings;
            var groups = addressableAssetSettings.groups;

            foreach (var index in Enumerable.Range(0, groups.Count)
                .Where(x => groups[x] == null)
                .Reverse())
            {
                groups.RemoveAt(index);
            }
        }

        public static void ClearAddressing()
        {
            try
            {
                Finalize(new Work());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Debug.Log($"Clear addressing finished.");
            }

        }

        private static bool VerifySymbol(Group group)
        {
            if (string.IsNullOrEmpty(group.symbol))
            {
                return true;
            }

            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup, out var defines);
            return defines.Contains(group.symbol);
        }

        private ParallelQuery<(string guid, string path, string label)> FindAssetsQuery(Group group)
        {
            var folderPaths = group.searchInFolders.Select(AssetDatabase.GetAssetPath).ToArray();
            var query = AssetDatabase.FindAssets(group.filter, folderPaths)
                .Select(x => (guid: x, path: AssetDatabase.GUIDToAssetPath(x)))
                .ToArray()
                .AsParallel()
                .Where(x => !x.path.Contains("/Editor/"))
                .Where(x => !File.GetAttributes(x.path).HasFlag(FileAttributes.Directory));

            if (!string.IsNullOrEmpty(group.pattern))
            {
                query = query.Where(x => Regex.IsMatch(x.path, group.pattern));
            }

            if (ignorePathPatterns.Length > 0)
            {
                query = caseInsensitivePathPatterns ?
                    query.Where(x => !ignorePathPatterns.Any(y => Regex.IsMatch(x.path.ToLower(), y.ToLower()))) :
                    query.Where(x => !ignorePathPatterns.Any(y => Regex.IsMatch(x.path, y)));
            }

            if (includePathPatterns.Length > 0)
            {
                query = caseInsensitivePathPatterns ?
                    query.Where(x => includePathPatterns.Any(y => Regex.IsMatch(x.path.ToLower(), y.ToLower()))) :
                    query.Where(x => includePathPatterns.Any(y => Regex.IsMatch(x.path, y)));
            }

            return query.Select(x => (x.guid, x.path, group.Label(x.path)));
        }
    }
}
