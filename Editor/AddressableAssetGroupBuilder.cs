using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AddressableAssetGroupBuilder
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

        /// <summary>
        /// True while Build / BuildAll is running. Used by the auto addressing postprocessor to avoid re-entrancy.
        /// </summary>
        public static bool IsBuilding { get; internal set; }

        public enum AddressNamingMode
        {
            FullPath = 0,
            FileName = 1,
            FileNameWithoutExtension = 2,
            FolderGuidAndFileName = 3,
            FolderGuidAndFileNameWithoutExtension = 4,

            // 5 = Dynamic (removed: build-order dependent numbering cannot be reproduced by incremental addressing)
            Blank = 6,
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

            [Tooltip("Specify any group setting such as PackTogether, PackTogetherByLabel, PackSeparately, etc.")]
            public AddressableAssetGroupTemplate template;

            public string summary;

            [Tooltip("Required scripting define symbol, like UNITY_EDITOR for only editor environment.")]
            public string symbol;

            [Tooltip("Address Naming Mode")]
            public AddressNamingMode namingMode;

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

            public string Label(string path) =>
                (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(pattern))
                    ? label
                    : Regex.Replace(path, pattern, label);

            public string Address(string path) =>
                namingMode switch
                {
                    AddressNamingMode.FullPath => path,
                    AddressNamingMode.FileName => Path.GetFileName(path),
                    AddressNamingMode.FileNameWithoutExtension => Path.GetFileNameWithoutExtension(path),
                    AddressNamingMode.FolderGuidAndFileName => $"{GetParentFolderGuid(path)}/{Path.GetFileName(path)}",
                    AddressNamingMode.FolderGuidAndFileNameWithoutExtension =>
                        $"{GetParentFolderGuid(path)}/{Path.GetFileNameWithoutExtension(path)}",
                    AddressNamingMode.Blank => "_",
                    _ => throw new NotSupportedException(
                        $"Unsupported naming mode [{(int)namingMode}] at group [{groupName}]. (Dynamic mode has been removed)"),
                };

            /// <summary>
            /// All labels for the path: group label + matching additional labels, split by ',', trimmed and distinct.
            /// </summary>
            public string[] Labels(string path)
            {
                IEnumerable<string> labels = new[] { Label(path) };
                labels = labels.Concat(additionalLabels
                    .Where(x => !string.IsNullOrEmpty(x.label) && !string.IsNullOrEmpty(x.pattern) &&
                                Regex.IsMatch(path, x.pattern))
                    .Select(x => Regex.Replace(path, x.pattern, x.label)));

                return labels
                    .Where(static x => !string.IsNullOrEmpty(x))
                    .SelectMany(static x => x.Split(','))
                    .Select(static x => x.Trim())
                    .Where(static x => x.Length > 0)
                    .Distinct()
                    .ToArray();
            }

            public string[] SearchFolderPaths() =>
                searchInFolders
                    .Where(static x => x != null)
                    .Select(AssetDatabase.GetAssetPath)
                    .Where(static x => !string.IsNullOrEmpty(x))
                    .ToArray();

            public bool IsInSearchFolders(string path)
            {
                var hasFolder = false;
                foreach (var folder in searchInFolders)
                {
                    if (folder == null) continue;
                    var folderPath = AssetDatabase.GetAssetPath(folder);
                    if (string.IsNullOrEmpty(folderPath)) continue;
                    hasFolder = true;
                    if (path.Length > folderPath.Length &&
                        path.StartsWith(folderPath, StringComparison.Ordinal) &&
                        path[folderPath.Length] == '/')
                    {
                        return true;
                    }
                }

                return !hasFolder;
            }

            private static string GetParentFolderGuid(string path) =>
                AssetDatabase.AssetPathToGUID(Path.GetDirectoryName(path).Replace('\\', '/'));
        }

        /// <summary>
        /// Result of resolving one asset path: which group it belongs to, and with what address / labels.
        /// Shared by the full build and the incremental (auto addressing) path.
        /// </summary>
        public readonly struct Resolution
        {
            public readonly AddressableAssetGroupBuilder builder;
            public readonly Group group;
            public readonly string address;
            public readonly string[] labels;

            public Resolution(AddressableAssetGroupBuilder builder, Group group, string path)
            {
                this.builder = builder;
                this.group = group;
                address = group.Address(path);
                labels = group.Labels(path);
            }

            public string GroupName => group.groupName;
        }

        /// <summary>
        /// Per-run caches used when resolving individual paths (incremental addressing).
        /// The FindAssets filter (e.g. "t:Prefab") cannot be evaluated from a path, so it is evaluated
        /// once per (group, directory) and cached as a guid set.
        /// </summary>
        public sealed class ResolveContext
        {
            private readonly Dictionary<(Group group, string directory), HashSet<string>> filterCache = new();
            private readonly Dictionary<Group, bool> symbolCache = new();

            public bool VerifySymbol(Group group)
            {
                if (!symbolCache.TryGetValue(group, out var ok))
                {
                    ok = AddressableAssetGroupBuilder.VerifySymbol(group);
                    symbolCache.Add(group, ok);
                }

                return ok;
            }

            public bool MatchesFilter(Group group, string guid, string path)
            {
                if (string.IsNullOrEmpty(group.filter))
                {
                    return true;
                }

                var directory = (Path.GetDirectoryName(path) ?? string.Empty).Replace('\\', '/');
                var key = (group, directory);
                if (!filterCache.TryGetValue(key, out var guids))
                {
                    guids = new HashSet<string>(AssetDatabase.FindAssets(group.filter, new[] { directory }));
                    filterCache.Add(key, guids);
                }

                return guids.Contains(guid);
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
                if (builder == null)
                {
                    return;
                }

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
                return groupNameMap.All(static x =>
                {
                    var missingList = x.Value
                        .Where(static y => y.Group.template == null)
                        .ToArray();

                    if (missingList.Length > 0)
                    {
                        foreach (var missing in missingList)
                        {
                            Debug.LogError(
                                $"Missing group template [{missing.Group.groupName}] in [{missing.Builder.name}]",
                                missing.Builder);
                        }

                        return false;
                    }

                    var first = x.Value.First().Group.template;

                    if (x.Value.Any(y => y.Group.template != first))
                    {
                        foreach ((Group Group, AddressableAssetGroupBuilder Builder) in x.Value)
                        {
                            Debug.LogError(
                                $"Ambiguous groups templates [{Group.template.name}] at [{Group.groupName}] in [{Builder.name}]",
                                Builder);
                        }

                        return false;
                    }

                    return true;
                });
            }
        }

        #region Matching

        /// <summary>
        /// Every path based condition of a group (everything except the FindAssets filter).
        /// Used by both the full build enumeration and the incremental resolver.
        /// </summary>
        public bool MatchesPath(Group group, string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains("/Editor/"))
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(path))
            {
                return false;
            }

            if (!group.IsInSearchFolders(path))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(group.pattern) && !Regex.IsMatch(path, group.pattern))
            {
                return false;
            }

            if (ignorePathPatterns.Length > 0 && MatchesAny(ignorePathPatterns, path))
            {
                return false;
            }

            if (includePathPatterns.Length > 0 && !MatchesAny(includePathPatterns, path))
            {
                return false;
            }

            return true;
        }

        private bool MatchesAny(string[] patterns, string path)
        {
            var options = caseInsensitivePathPatterns ? RegexOptions.IgnoreCase : RegexOptions.None;
            foreach (var pattern in patterns)
            {
                if (!string.IsNullOrEmpty(pattern) && Regex.IsMatch(path, pattern, options)) return true;
            }

            return false;
        }

        /// <summary>
        /// Resolve a single path against this builder's groups. Later groups win (same as the full build).
        /// </summary>
        public bool TryResolve(string path, string guid, ResolveContext context, out Resolution result)
        {
            result = default;
            var found = false;
            foreach (var group in groups)
            {
                if (!context.VerifySymbol(group))
                {
                    continue;
                }

                if (!MatchesPath(group, path))
                {
                    continue;
                }

                if (!context.MatchesFilter(group, guid, path))
                {
                    continue;
                }

                result = new Resolution(this, group, path);
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Enumerate every (guid, resolution) this builder produces. Later groups overwrite earlier ones,
        /// so the returned map is exactly what Build applies.
        /// </summary>
        public void Enumerate(Dictionary<string, (string path, Resolution resolution)> into,
            Action<Group, string, float> progress = null)
        {
            foreach (var group in groups.Where(VerifySymbol))
            {
                var assets = FindAssets(group).ToArray();
                for (var index = 0; index < assets.Length; ++index)
                {
                    var (guid, path) = assets[index];
                    progress?.Invoke(group, path, (index + 1) / (float)assets.Length);
                    into[guid] = (path, new Resolution(this, group, path));
                }
            }
        }

        private IEnumerable<(string guid, string path)> FindAssets(Group group)
        {
            return AssetDatabase.FindAssets(group.filter, group.SearchFolderPaths())
                .Select(static x => (guid: x, path: AssetDatabase.GUIDToAssetPath(x)))
                .Where(x => MatchesPath(group, x.path));
        }

        #endregion

        #region Build

        public int Test()
        {
            var map = new Dictionary<string, (string path, Resolution resolution)>();
            Enumerate(map);
            foreach (var (path, resolution) in map.Values.OrderBy(static x => x.path))
            {
                Debug.Log(
                    $"asset: {path}\nlabel: {string.Join(",", resolution.labels)}\ngroup: {resolution.GroupName}\naddress: {resolution.address}");
            }

            Debug.Log($"{map.Count} asset entries had found at {this.name}");
            return map.Count;
        }

        public void Build()
        {
            var stopwatch = Stopwatch.StartNew();
            var work = new Work();
            IsBuilding = true;
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
                IsBuilding = false;
                EditorUtility.ClearProgressBar();
                Debug.Log($"AddressablesGroupBuilder finished. {stopwatch.Elapsed.TotalSeconds:F3} seconds");
            }
        }

        public void Build(Work work)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;

            // make sure every group exists and has its template applied, even if it ends up empty
            foreach (var group in groups.Where(VerifySymbol))
            {
                GetOrCreateGroup(settings, group, work.groupMap);
            }

            var map = new Dictionary<string, (string path, Resolution resolution)>();
            Enumerate(map,
                (group, path, ratio) => EditorUtility.DisplayProgressBar($"[{name}][{group.groupName}]", path, ratio));

            foreach (var pair in map)
            {
                var guid = pair.Key;
                var resolution = pair.Value.resolution;
                var group = GetOrCreateGroup(settings, resolution.group, work.groupMap);
                var entry = settings.FindAssetEntry(guid);
                if (entry == null || entry.parentGroup != group)
                {
                    entry = settings.CreateOrMoveEntry(guid, group, true, false);
                }

                ApplyEntry(settings, entry, resolution);
                work.guids.Add(guid);
                work.labels.UnionWith(resolution.labels);
            }
        }

        #endregion

        /// <summary>
        /// Find or create the addressable group for a group policy and apply its template.
        /// </summary>
        /// <param name="applyTemplateToExisting">
        /// Full build: true (the template is the source of truth). Incremental: false, so that a single import
        /// does not rewrite the group's schemas and dirty the group asset needlessly.
        /// </param>
        public static AddressableAssetGroup GetOrCreateGroup(
            AddressableAssetSettings settings, Group policy, Dictionary<string, AddressableAssetGroup> cache,
            bool applyTemplateToExisting = true)
        {
            return GetOrCreateGroup(settings, policy, cache, applyTemplateToExisting, out _);
        }

        /// <param name="created">True when the group did not exist and was created (the settings asset changed).</param>
        public static AddressableAssetGroup GetOrCreateGroup(
            AddressableAssetSettings settings, Group policy, Dictionary<string, AddressableAssetGroup> cache,
            bool applyTemplateToExisting, out bool created)
        {
            created = false;
            if (cache.TryGetValue(policy.groupName, out var group))
            {
                return group;
            }

            group = settings.FindGroup(policy.groupName);
            if (group == null)
            {
                group = settings.CreateGroup(
                    policy.groupName, false, true, false, null, policy.template.GetTypes());
                policy.template.ApplyToAddressableAssetGroup(group);
                created = true;
            }
            else if (applyTemplateToExisting)
            {
                policy.template.ApplyToAddressableAssetGroup(group);
            }

            cache.Add(policy.groupName, group);
            return group;
        }

        /// <summary>
        /// Set address and replace labels of an entry. Labels not in the resolution are removed from the entry
        /// so that incremental updates never leave stale labels behind.
        /// </summary>
        /// <returns>True when the settings asset itself was modified (a new label was registered).</returns>
        public static bool ApplyEntry(AddressableAssetSettings settings, AddressableAssetEntry entry,
            in Resolution resolution)
        {
            entry.SetAddress(resolution.address, false);

            // Newer Addressables versions return a copy from labels. Mutate through SetLabel
            // so the actual labels and the entry hash are updated on every supported version.
            List<string> labelsToRemove = null;
            foreach (var label in entry.labels)
            {
                if (Array.IndexOf(resolution.labels, label) >= 0) continue;
                (labelsToRemove ??= new List<string>()).Add(label);
            }

            if (labelsToRemove != null)
            {
                foreach (var label in labelsToRemove) entry.SetLabel(label, false, false, false);
            }

            var settingsModified = false;
            var knownLabels = settings.GetLabels();
            foreach (var label in resolution.labels)
            {
                if (!knownLabels.Contains(label))
                {
                    settings.AddLabel(label, false);
                    settingsModified = true;
                }

                entry.SetLabel(label, true, false, false);
            }

            return settingsModified;
        }

        public static void Finalize(Work work, string[] keepGroupNamesRegexPattern = null)
        {
            var addressableAssetSettings = AddressableAssetSettingsDefaultObject.Settings;

            EditorUtility.DisplayProgressBar("Finalize", "", 1f);

            var keepGroupNames = new HashSet<string>();
            var keepGroupLabels = new HashSet<string>();
            if (keepGroupNamesRegexPattern != null)
            {
                foreach (var group in addressableAssetSettings.groups)
                {
                    if (group == null || !keepGroupNamesRegexPattern.Any(x => Regex.IsMatch(group.Name, x)))
                    {
                        continue;
                    }

                    keepGroupNames.Add(group.Name);
                    foreach (var label in group.entries.SelectMany(static x => x.labels))
                    {
                        keepGroupLabels.Add(label);
                    }
                }
            }

            // remove unused entries
            var entries = new List<AddressableAssetEntry>();
            addressableAssetSettings.GetAllAssets(entries, false);
            foreach (var entry in entries.Where(x => !work.guids.Contains(x.guid)))
            {
                if (entry.parentGroup == null || keepGroupNames.Contains(entry.parentGroup.Name))
                {
                    continue;
                }

                if (!work.groupMap.ContainsKey(entry.parentGroup.Name))
                {
                    work.groupMap.Add(entry.parentGroup.Name, entry.parentGroup);
                }

                entry.parentGroup.RemoveAssetEntry(entry, false);
            }

            // remove unused labels
            foreach (var label in addressableAssetSettings.GetLabels().Where(x => !work.labels.Contains(x)).ToArray())
            {
                if (keepGroupLabels.Contains(label))
                {
                    continue;
                }

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

            for (var index = groups.Count - 1; index >= 0; --index)
            {
                if (groups[index] == null) groups.RemoveAt(index);
            }
        }

        public static void ClearAddressing(string[] keepGroupNamesRegexPattern = null)
        {
            IsBuilding = true;
            try
            {
                Finalize(new Work(), keepGroupNamesRegexPattern);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                IsBuilding = false;
                EditorUtility.ClearProgressBar();
                Debug.Log($"Clear addressing finished.");
            }
        }

        public static bool VerifySymbol(Group group)
        {
            if (string.IsNullOrEmpty(group.symbol))
            {
                return true;
            }

            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            var defines =
                PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup));
            return defines.Split(';').Select(static x => x.Trim()).Contains(group.symbol);
        }
    }
}