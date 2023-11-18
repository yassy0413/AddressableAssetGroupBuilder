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
        public string summary;
        [Tooltip("Path matching is case-insensitive")]
        public bool caseInsensitivePathPatterns = false;
        [Tooltip("Exclude paths matching any of the regular expressions")]
        public string[] ignorePathPatterns = Array.Empty<string>();
        [Tooltip("Contains paths matching any of the regular expressions (if not empty)")]
        public string[] includePathPatterns = Array.Empty<string>();
        public Group[] groups = Array.Empty<Group>();

        public enum AddressNamingMode
        {
            FullPath,
            FileName,
            FileNameWithoutExtension,
            FolderGuidAndFileName,
            FolderGuidAndFileNameWithoutExtension,
            Dynamic,
            Blank,
        }

        [Serializable]
        public sealed class AdditionalLabel
        {
            [Tooltip("Multiple labels can be defined by separating them with ','")]
            public string label;
            [Tooltip("Regular expression path matching. The resulting variables can be used in label.")]
            public string pattern;
        }

        [Serializable]
        public sealed class Group
        {
            public string groupName;
            public string summary;
            [Tooltip("Address Naming Mode")]
            public AddressNamingMode namingMode;
            [Tooltip("Specify any group setting such as PackTogether, PackTogetherByLabel, PackSeparately, etc.")]
            public AddressableAssetGroupTemplate template;
            [Tooltip("Required scripting define symbol, like UNITY_EDITOR for only editor environment.")]
            public string symbol;
            [Tooltip("Multiple labels can be defined by separating them with ','")]
            public string label;
            [Tooltip("Filter for FindAssets")]
            public string filter;
            [Tooltip("Add regular expression path matching. The resulting variables can be used in label.")]
            public string pattern;
            [Tooltip("Target folders")]
            public DefaultAsset[] searchInFolders = Array.Empty<DefaultAsset>();
            [Tooltip("Additional labels for pattern matching targets.")]
            public AdditionalLabel[] additionalLabels = Array.Empty<AdditionalLabel>();

            public Dictionary<string, int> DynamicPathNumberMap { get; } = new ();

            public string Label(string path) =>
                (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(pattern)) ?
                    label : Regex.Replace(path, pattern, label);

            public string Address(string path) =>
                namingMode switch
                {
                    AddressNamingMode.FullPath => path,
                    AddressNamingMode.FileName => Path.GetFileName(path),
                    AddressNamingMode.FileNameWithoutExtension => Path.GetFileNameWithoutExtension(path),
                    AddressNamingMode.FolderGuidAndFileName => $"{GetParentFolderGuid(path)}/{Path.GetFileName(path)}",
                    AddressNamingMode.FolderGuidAndFileNameWithoutExtension => $"{GetParentFolderGuid(path)}/{Path.GetFileNameWithoutExtension(path)}",
                    AddressNamingMode.Dynamic => GetDynamicPathNumber(path),
                    AddressNamingMode.Blank => "_",
                    _ => throw new NotImplementedException(),
                };

            public string[] ValidAdditionalLabels(string path) =>
                additionalLabels
                .Where(x => !string.IsNullOrEmpty(x.label) && Regex.IsMatch(path, x.pattern))
                .Select(x => Regex.Replace(path, x.pattern, x.label))
                .ToArray();

            private string GetParentFolderGuid(string path) =>
                AssetDatabase.AssetPathToGUID(Path.GetDirectoryName(path));

            private string GetDynamicPathNumber(string path)
            {
                if (!DynamicPathNumberMap.TryGetValue(path, out var number))
                {
                    number = DynamicPathNumberMap.Count;
                    DynamicPathNumberMap.Add(path, number);
                }
                return number.ToString();
            }
        }

        public sealed class Work
        {
            public readonly Dictionary<string, AddressableAssetGroup> groupMap = new();
            public readonly HashSet<string> labels = new();
            public readonly HashSet<string> guids = new();
        }

        public sealed class GroupVerifier
        {
            private Dictionary<string, List<(Group Group, AddressableAssetGroupBuilder Builder)>> groupNameMap = new();

            public void Join(AddressableAssetGroupBuilder builder)
            {
                foreach (var groupPolicy in builder.groups)
                {
                    if (!groupNameMap.TryGetValue(groupPolicy.groupName, out var list))
                    {
                        list = new List<(Group Group, AddressableAssetGroupBuilder Builder)>();
                        groupNameMap.Add(groupPolicy.groupName, list);
                    }

                    list.Add((groupPolicy, builder));
                }
            }

            public bool Verify()
            {
                return groupNameMap.All(x =>
                {
                    var missingList = x.Value
                        .Where(y => y.Group.template == null)
                        .ToArray();

                    if (missingList.Length > 0)
                    {
                        foreach (var missing in missingList)
                        {
                            Debug.LogError($"Missing group template [{missing.Group.groupName}] in [{missing.Builder.name}]", missing.Builder);
                        }
                        return false;
                    }

                    var first = x.Value.First().Group.template;

                    if (x.Value.Any(y => y.Group.template != first))
                    {
                        foreach ((Group Group, AddressableAssetGroupBuilder Builder) in x.Value)
                        {
                            Debug.LogError($"Ambiguous groups templates [{Group.template.name}] at [{Group.groupName}] in [{Builder.name}]", Builder);
                        }
                        return false;
                    }

                    return true;
                });
            }
        }

        public int Test()
        {
            var count = 0;
            foreach (var groupPolicy in groups.Where(VerifySymbol))
            {
                foreach (var asset in FindAssetsQuery(groupPolicy).OrderBy(x => x.path))
                {
                    var labels = asset.label.Split(',').Concat(groupPolicy.ValidAdditionalLabels(asset.path)).Distinct();
                    var address = groupPolicy.Address(asset.path);
                    Debug.Log($"asset: {asset.path}\nlabel: {string.Join(",", labels)}\ngroup: {groupPolicy.groupName}\naddress: {address}");
                    ++count;
                }
            }
            Debug.Log($"{count} asset entries had found at {this.name}");
            return count;
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
                var title = $"[{name}][{groupPolicy.groupName}]";

                groupPolicy.DynamicPathNumberMap.Clear();

                // create group
                if (!work.groupMap.TryGetValue(groupPolicy.groupName, out var group))
                {
                    group = addressableAssetSettings.FindGroup(groupPolicy.groupName);
                    if (group == null)
                    {
                        group = addressableAssetSettings.CreateGroup(
                            groupPolicy.groupName, false, true, false, null, groupPolicy.template.GetTypes());
                    }
                    groupPolicy.template.ApplyToAddressableAssetGroup(group);
                    work.groupMap.Add(groupPolicy.groupName, group);
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

                    entry.SetAddress(groupPolicy.Address(path), false);

                    void AddLabels(string labels)
                    {
                        foreach (var label in labels.Split(','))
                        {
                            addressableAssetSettings.AddLabel(label, false);
                            entry.labels.Add(label);
                            work.labels.Add(label);
                        }
                    }

                    if (!string.IsNullOrEmpty(label))
                    {
                        AddLabels(label);
                    }

                    foreach (var additionalLabel in groupPolicy.ValidAdditionalLabels(path))
                    {
                        AddLabels(additionalLabel);
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
