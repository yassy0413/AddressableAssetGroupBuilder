using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AddressableAssetGroupBuilder
{
    /// <summary>
    /// Collects imports and drains a bounded incremental batch per editor update.
    /// Pending paths survive assembly reloads within the current editor session.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class AutoAddressingPostprocessor : AssetPostprocessor
    {
        private const string PendingKey = "AddressableAssetGroupBuilder.AutoAddressing.Pending";
        // Queue provides O(1) draining; dictionary coalesces each path to its latest state.
        private static readonly Queue<string> pendingOrder = new();
        private static readonly Dictionary<string, bool> pending = new(StringComparer.Ordinal);
        private static readonly List<string> changed = new();
        private static readonly List<string> removed = new();
        private static bool flushScheduled;
        private static bool definitionChangeNotified;
        private static double retryAfter;

        [Serializable]
        private sealed class Snapshot
        {
            public string[] changedPaths;
            public string[] removedPaths;
        }

        static AutoAddressingPostprocessor()
        {
            // No AssetDatabase access during initialization: restore only the pending paths.
            RestorePending();
            AssemblyReloadEvents.beforeAssemblyReload += SavePending;
        }

        private static void RestorePending()
        {
            var json = SessionState.GetString(PendingKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                var snapshot = JsonUtility.FromJson<Snapshot>(json);
                foreach (var path in snapshot.removedPaths) Enqueue(path, false);
                foreach (var path in snapshot.changedPaths) Enqueue(path, true);
                SessionState.EraseString(PendingKey);
            }

            ScheduleFlush();
        }

        private static void SavePending()
        {
            if (pending.Count == 0)
            {
                SessionState.EraseString(PendingKey);
                return;
            }

            // Serialize once at the reload boundary, not once per chunk of a large import.
            var snapshot = new Snapshot
            {
                changedPaths = pending.Where(static pair => pair.Value).Select(static pair => pair.Key).ToArray(),
                removedPaths = pending.Where(static pair => !pair.Value).Select(static pair => pair.Key).ToArray(),
            };
            SessionState.SetString(PendingKey, JsonUtility.ToJson(snapshot));
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths,
            bool didDomainReload)
        {
            if (Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess()) return;

            // Reload callbacks can contain real imports too; never skip their change arrays.
            if (didDomainReload) AutoAddressing.InvalidateCache();
            if (importedAssets.Concat(deletedAssets).Concat(movedAssets).Concat(movedFromAssetPaths)
                .Any(AutoAddressing.IsDefinitionAsset))
            {
                AutoAddressing.InvalidateCache();
                if (!definitionChangeNotified)
                {
                    definitionChangeNotified = true;
                    Debug.Log(
                        "[AutoAddressing] Group Builder definitions changed. Existing entries are not rebuilt automatically; run Build on the batch when needed.");
                }
            }

            // Explicitly disabled: do not collect new work. Busy: collect now and apply later.
            if (!AutoAddressing.Enabled || AutoAddressing.ActiveBatch == null) return;

            // Within one callback, a replacement/move destination wins over the old path.
            // Across callbacks, the latest notification wins (including import followed by delete).
            Collect(deletedAssets, false);
            Collect(movedFromAssetPaths, false);
            Collect(importedAssets, true);
            Collect(movedAssets, true);
            ScheduleFlush();
        }

        private static void Collect(string[] paths, bool isChanged)
        {
            foreach (var path in paths)
            {
                // Output and definition assets are still excluded during our own saves.
                if (AutoAddressing.IsCandidate(path)) Enqueue(path, isChanged);
            }
        }

        private static void Enqueue(string path, bool isChanged)
        {
            if (!pending.ContainsKey(path)) pendingOrder.Enqueue(path);
            pending[path] = isChanged;
        }

        private static void ScheduleFlush()
        {
            if (flushScheduled || pending.Count == 0) return;
            flushScheduled = true;
            EditorApplication.update += Flush;
        }

        private static void Flush()
        {
            if (EditorApplication.timeSinceStartup < retryAfter) return;
            if (!AutoAddressing.IsAvailable || AutoAddressing.ActiveBatch == null)
            {
                // Avoid repeated settings/batch lookup every frame during a long pause.
                retryAfter = EditorApplication.timeSinceStartup + 0.25;
                return;
            }

            var batch = AutoAddressing.ActiveBatch;
            changed.Clear();
            removed.Clear();
            var limit = AutoAddressing.MaxAssetsPerUpdate;
            while (pendingOrder.Count > 0 && changed.Count + removed.Count < limit)
            {
                var path = pendingOrder.Dequeue();
                var isChanged = pending[path];
                pending.Remove(path);
                (isChanged ? changed : removed).Add(path);
            }

            // Remove only this chunk before applying so reentrant imports of the same path
            // can enqueue fresh work. The remaining backlog is never cleared by a guard.
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var result = AutoAddressingProcessor.Apply(batch, changed, removed);
                if (result.Changes > 0)
                {
                    Debug.Log($"[AutoAddressing] {result} ({stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
                }
            }
            finally
            {
                changed.Clear();
                removed.Clear();
                if (pending.Count == 0)
                {
                    EditorApplication.update -= Flush;
                    flushScheduled = false;
                }
            }
        }
    }
}
