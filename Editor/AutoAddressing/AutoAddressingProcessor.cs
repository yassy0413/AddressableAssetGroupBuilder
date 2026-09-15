using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableAssetGroupBuilder
{
    /// <summary>
    /// Incremental addressing: applies the batch's rules to a set of changed / removed asset paths only.
    /// Uses the same resolution as the full build, is idempotent, and never performs project-wide cleanup
    /// (unused labels / groups are only removed by a full Build).
    /// </summary>
    public static class AutoAddressingProcessor
    {
        public static bool IsApplying { get; private set; }

        public readonly struct Result
        {
            public readonly int added;
            public readonly int updated;
            public readonly int removed;
            public readonly int untouched;

            public Result(int added, int updated, int removed, int untouched)
            {
                this.added = added;
                this.updated = updated;
                this.removed = removed;
                this.untouched = untouched;
            }

            public int Changes => added + updated + removed;

            public override string ToString() =>
                $"added: {added}, updated: {updated}, removed: {removed}, untouched: {untouched}";
        }

        /// <summary>
        /// Apply the batch's rules to the given paths.
        /// changedPaths: imported assets and move destinations. removedPaths: deleted assets and move sources.
        /// </summary>
        public static Result Apply(AddressableAssetGroupBuilderBatch batch, IEnumerable<string> changedPaths,
            IEnumerable<string> removedPaths)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var context = new AddressableAssetGroupBuilder.ResolveContext();
            var groupCache = new Dictionary<string, AddressableAssetGroup>();
            var touched = new HashSet<AddressableAssetGroup>();
            var settingsModified = false; // only a new label or a new group changes the settings asset
            int added = 0, updated = 0, removed = 0, untouched = 0;

            IsApplying = true;
            try
            {
                // removed first, so a move never leaves a stale entry behind even if Addressables' own
                // postprocessor did not handle it
                foreach (var path in removedPaths)
                {
                    var entry = FindEntryForMissingPath(settings, path);
                    if (entry == null)
                    {
                        continue;
                    }

                    if (RemoveEntry(entry, touched))
                    {
                        ++removed;
                    }
                }

                foreach (var path in changedPaths)
                {
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid) || AssetDatabase.GUIDToAssetPath(guid) != path)
                    {
                        continue; // not an existing asset (any more)
                    }

                    var entry = settings.FindAssetEntry(guid);

                    if (batch.TryResolve(path, guid, context, out var resolution))
                    {
                        var group = AddressableAssetGroupBuilder.GetOrCreateGroup(settings, resolution.group,
                            groupCache, false, out var created);
                        settingsModified |= created;
                        var isNew = entry == null;

                        if (entry != null && entry.parentGroup != group)
                        {
                            touched.Add(entry.parentGroup);
                        }

                        if (entry == null || entry.parentGroup != group)
                        {
                            entry = settings.CreateOrMoveEntry(guid, group, true, false);
                        }
                        else if (entry.address == resolution.address && SameLabels(entry, resolution.labels))
                        {
                            ++untouched;
                            continue;
                        }

                        settingsModified |= AddressableAssetGroupBuilder.ApplyEntry(settings, entry, resolution);
                        touched.Add(group);
                        if (isNew) ++added;
                        else ++updated;
                    }
                    else if (entry != null)
                    {
                        // no longer matches any rule: mirror what Finalize would do on a full build
                        if (entry.parentGroup != null && batch.IsKeepGroup(entry.parentGroup.Name))
                        {
                            ++untouched;
                            continue;
                        }

                        if (RemoveEntry(entry, touched))
                        {
                            ++removed;
                        }
                    }
                }

                if (touched.Count > 0)
                {
                    // one save (= one reimport) per group whose entries actually changed
                    foreach (var group in touched.Where(static x => x != null))
                    {
                        group.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, false,
                            true); // groupModified: mark the group asset dirty, no event yet
                        AssetDatabase.SaveAssetIfDirty(group);
                    }

                    // the settings asset is saved only when it really changed (new label / new group);
                    // the single BatchModification event is posted here either way
                    settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true,
                        settingsModified);
                    if (settingsModified)
                    {
                        AssetDatabase.SaveAssetIfDirty(settings);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                IsApplying = false;
            }

            return new Result(added, updated, removed, untouched);
        }

        private static bool SameLabels(AddressableAssetEntry entry, string[] labels)
        {
            var currentLabels = entry.labels;
            if (currentLabels.Count != labels.Length) return false;
            foreach (var label in labels)
            {
                if (!currentLabels.Contains(label)) return false;
            }

            return true;
        }

        private static bool RemoveEntry(AddressableAssetEntry entry, HashSet<AddressableAssetGroup> touched)
        {
            var group = entry.parentGroup;
            if (group == null)
            {
                return false;
            }

            group.RemoveAssetEntry(entry, false);
            touched.Add(group);
            return true;
        }

        /// <summary>
        /// Entry for a path that no longer exists (deleted, or a move source). Only returns an entry whose
        /// asset is really gone, so a moved asset (guid now at a new path) is left to the changed-path handling.
        /// </summary>
        private static AddressableAssetEntry FindEntryForMissingPath(AddressableAssetSettings settings, string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                // the guid map has already forgotten this path: fall back to scanning entries by their recorded path
                return settings.groups
                    .Where(static x => x != null)
                    .SelectMany(static x => x.entries)
                    .FirstOrDefault(x =>
                        x.AssetPath == path && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(x.guid)));
            }

            var currentPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(currentPath) && currentPath != path)
            {
                return null; // moved: handled as a changed path
            }

            return settings.FindAssetEntry(guid);
        }
    }
}