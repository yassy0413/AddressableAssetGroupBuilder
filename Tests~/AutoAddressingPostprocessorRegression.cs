// Standalone regression harness. Compile together with the production postprocessor only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using AddressableAssetGroupBuilder;

namespace UnityEngine
{
    public static class Application { public static bool isBatchMode; }
    public static class Debug { public static void Log(object value) { } }
    public static class JsonUtility
    {
        public static string ToJson(object value)
        {
            return new JavaScriptSerializer().Serialize(value.GetType().GetFields()
                .ToDictionary(f => f.Name, f => f.GetValue(value)));
        }
        public static T FromJson<T>(string json)
        {
            var value = (T)Activator.CreateInstance(typeof(T), true);
            foreach (var pair in new JavaScriptSerializer().Deserialize<Dictionary<string, string[]>>(json))
                typeof(T).GetField(pair.Key).SetValue(value, pair.Value);
            return value;
        }
    }
}
namespace UnityEditor
{
    public class AssetPostprocessor { }
    public sealed class InitializeOnLoadAttribute : Attribute { }
    public static class AssetDatabase
    {
        public static bool worker;
        public static bool IsAssetImportWorkerProcess() => worker;
    }
    public static class EditorApplication
    {
        public static Action update;
        public static double timeSinceStartup;
        public static void Tick() { timeSinceStartup += 1; update?.Invoke(); }
    }
    public static class AssemblyReloadEvents { public static Action beforeAssemblyReload; }
    public static class SessionState
    {
        private static readonly Dictionary<string, string> values = new();
        public static string GetString(string key, string fallback) => values.TryGetValue(key, out var v) ? v : fallback;
        public static void SetString(string key, string value) => values[key] = value;
        public static void EraseString(string key) => values.Remove(key);
    }
}
namespace AddressableAssetGroupBuilder
{
    public class AddressableAssetGroupBuilderBatch { }
    public static class AutoAddressing
    {
        public static bool Enabled = true, busy;
        public static int MaxAssetsPerUpdate = 500;
        public static AddressableAssetGroupBuilderBatch ActiveBatch = new();
        public static bool IsAvailable => Enabled && !busy;
        public static void InvalidateCache() { }
        public static bool IsDefinitionAsset(string p) => p.EndsWith("Definition.asset");
        public static bool IsCandidate(string p) => p.StartsWith("Assets/Input/");
    }
    public static class AutoAddressingProcessor
    {
        public static readonly List<(string[] changed, string[] removed)> calls = new();
        public static Action duringApply;
        public readonly struct Result { public int Changes => 0; }
        public static Result Apply(AddressableAssetGroupBuilderBatch batch, IEnumerable<string> changed, IEnumerable<string> removed)
        {
            calls.Add((changed.ToArray(), removed.ToArray()));
            var callback = duringApply; duringApply = null; callback?.Invoke();
            return default;
        }
    }
}
static class Regression
{
    static readonly Type Target = typeof(AutoAddressingPostprocessor);
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
    static int passed;
    static void Call(string method, params object[] args) => Target.GetMethod(method, Flags).Invoke(null, args);
    static void Set(string field, object value) => Target.GetField(field, Flags).SetValue(null, value);
    static void Import(string[] changed = null, string[] removed = null, bool reload = false)
        => Call("OnPostprocessAllAssets", changed ?? Array.Empty<string>(), removed ?? Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), reload);
    static string Path(int i) => "Assets/Input/" + i + ".asset";
    static void Tick() => UnityEditor.EditorApplication.Tick();
    static void Assert(bool value) { if (!value) throw new Exception("Regression failed"); }
    static void ClearMemory()
    {
        foreach (var field in new[] { "pendingOrder", "pending", "changed", "removed" })
        {
            var value = Target.GetField(field, Flags).GetValue(null);
            value.GetType().GetMethod("Clear").Invoke(value, null);
        }
        Set("flushScheduled", false); Set("retryAfter", 0d);
        UnityEditor.EditorApplication.update = null;
    }
    static void Run(string name, Action test)
    {
        ClearMemory(); Call("SavePending");
        AutoAddressing.Enabled = true; AutoAddressing.busy = false;
        AutoAddressing.ActiveBatch = new(); AutoAddressing.MaxAssetsPerUpdate = 500;
        AutoAddressingProcessor.calls.Clear(); AutoAddressingProcessor.duringApply = null;
        UnityEngine.Application.isBatchMode = false; UnityEditor.AssetDatabase.worker = false;
        test(); passed++; Console.WriteLine("PASS " + name);
    }
    static void Main()
    {
        Run("reload callback still imports payload", () => {
            Import(new[] { Path(1) }, reload: true); Tick(); Assert(AutoAddressingProcessor.calls.Single().changed.Single() == Path(1));
        });
        Run("busy at collection and flush retains work", () => {
            AutoAddressing.busy = true; Import(new[] { Path(1) }); Tick(); Assert(AutoAddressingProcessor.calls.Count == 0);
            AutoAddressing.busy = false; Tick(); Assert(AutoAddressingProcessor.calls.Count == 1);
        });
        Run("disabled between collection and flush resumes existing work only", () => {
            Import(new[] { Path(1) }); AutoAddressing.Enabled = false; Import(new[] { Path(2) }); Tick();
            Assert(AutoAddressingProcessor.calls.Count == 0); AutoAddressing.Enabled = true; Tick();
            Assert(AutoAddressingProcessor.calls.Single().changed.SequenceEqual(new[] { Path(1) }));
        });
        Run("temporarily missing active batch retains pending work", () => {
            Import(new[] { Path(1) }); AutoAddressing.ActiveBatch = null; Tick(); Assert(AutoAddressingProcessor.calls.Count == 0);
            AutoAddressing.ActiveBatch = new(); Tick(); Assert(AutoAddressingProcessor.calls.Count == 1);
        });
        Run("reload restores pending changed and deleted paths", () => {
            Import(new[] { Path(1) }, new[] { Path(2) }); Call("SavePending"); ClearMemory(); Call("RestorePending"); Tick();
            Assert(AutoAddressingProcessor.calls.Single().changed.Single() == Path(1));
            Assert(AutoAddressingProcessor.calls.Single().removed.Single() == Path(2));
        });
        Run("import followed by delete uses final state", () => {
            Import(new[] { Path(1) }); Import(removed: new[] { Path(1) }); Tick();
            Assert(AutoAddressingProcessor.calls.Single().removed.Single() == Path(1)); Assert(AutoAddressingProcessor.calls[0].changed.Length == 0);
        });
        Run("delete followed by recreation uses final state", () => {
            Import(removed: new[] { Path(1) }); Import(new[] { Path(1) }); Tick();
            Assert(AutoAddressingProcessor.calls.Single().changed.Single() == Path(1)); Assert(AutoAddressingProcessor.calls[0].removed.Length == 0);
        });
        Run("mixed 1201 paths drain in bounded chunks", () => {
            Import(Enumerable.Range(0, 601).Select(Path).ToArray(), Enumerable.Range(601, 600).Select(Path).ToArray());
            Tick(); Assert(AutoAddressingProcessor.calls.Count == 1); Tick(); Tick();
            Assert(AutoAddressingProcessor.calls.Select(c => c.changed.Length + c.removed.Length).SequenceEqual(new[] { 500, 500, 201 }));
            Assert(AutoAddressingProcessor.calls.SelectMany(c => c.changed.Concat(c.removed)).Distinct().Count() == 1201);
            Assert(UnityEditor.EditorApplication.update == null);
        });
        Run("reload between chunks does not replay completed work", () => {
            Import(Enumerable.Range(0, 501).Select(Path).ToArray()); Tick(); Call("SavePending"); ClearMemory(); Call("RestorePending"); Tick();
            Assert(AutoAddressingProcessor.calls.SelectMany(c => c.changed).Count() == 501);
            Assert(AutoAddressingProcessor.calls.SelectMany(c => c.changed).Distinct().Count() == 501);
        });
        Run("reentrant import of current path survives chunk completion", () => {
            Import(new[] { Path(1) }); AutoAddressingProcessor.duringApply = () => Import(new[] { Path(1) }); Tick(); Tick();
            Assert(AutoAddressingProcessor.calls.Count == 2);
        });
        Run("output imports do not enqueue recursive work", () => {
            Import(new[] { "Assets/AddressableAssetsData/Group.asset" }); Tick(); Assert(AutoAddressingProcessor.calls.Count == 0);
        });
        Run("batch and worker processes do not collect", () => {
            UnityEngine.Application.isBatchMode = true; Import(new[] { Path(1) }); UnityEngine.Application.isBatchMode = false;
            UnityEditor.AssetDatabase.worker = true; Import(new[] { Path(2) }); Tick(); Assert(AutoAddressingProcessor.calls.Count == 0);
        });
        Console.WriteLine(passed + " regressions passed");
    }
}
