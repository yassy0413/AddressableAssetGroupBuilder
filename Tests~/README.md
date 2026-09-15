# Queue regression tests

Run on macOS using the compiler/runtime bundled with a Unity installation:

```sh
python3 'Tests~/run_regressions.py' '/Applications/Unity/Hub/Editor/6000.5.1f1/Unity.app/Contents'
```

The harness compiles the production `AutoAddressingPostprocessor.cs` with small stand-ins for
Unity's event loop, SessionState, JSON serialization, candidate filtering and the applying processor.
It checks queue behavior: reload payloads and restoration, temporary pauses, disabled/no-batch
states, event coalescing, mixed changed/deleted chunk limits, no completed-work replay, reentrant
imports, and excluded output/process contexts. The runner keeps all binaries in a temporary folder.

These are not Unity EditMode tests: they do not verify actual AssetDatabase imports, saves,
SessionState lifecycle, the real candidate predicate, or frame timings. In Unity, additionally test
an import containing scripts and assets together, reload between chunks, disabling/re-enabling
with pending work, and more than 500 changed/deleted assets. Confirm that no full Build/cleanup
runs and that the final addresses and labels match the rules after the queue drains.

The trailing `~` keeps this standalone harness out of Unity's normal asset compilation.
