# AotGcRepro: browser-wasm AOT loses a `this` across a minor GC

A minimal repro for a Mono **llvm-only AOT** (browser-wasm) bug. A reference that is passed as an argument is kept on
no shadow-stack slot, so the conservative GC does not pin the object. A minor GC moves it, and the callee continues
with a stale `this`.

In a real app this shows up as a `NullReferenceException` on a non-null object, as wrong data, as
`ArrayTypeMismatchException` in `stelemref`, or as `RuntimeError: null function` / `memory access out of bounds`.

## The shape

```csharp
public (Func<string> Said, Func<int> Count) Build(int count) => (() => Summary, () => count + Values.Length);

public string Summary => Label + ": " + string.Join("; ", Values.Select(v => Label + " " + v))
    + ". Turned " + Math.Round(TurnNow) + "°";
```

1. `Build` makes one display class that holds `<>4__this` and `count`.
2. The lambda `<>c__DisplayClass8_0.<Build>b__0` loads `<>4__this` from the closure and passes it **straight as the
   argument**. It stores only the call's *result* into its shadow-stack frame:
   ```wat
   (i32.store offset=12 (local.get $2)
     (local.tee $0 (call $8 (i32.load offset=8 (local.get $0)) (local.get $0))))
   ```
3. `Chart.get_Summary` never stores its `this` (`$0`) into its own frame. It **allocates first** (the concat array),
   then reads `this`, allocates again (the lambda, `Join`), and reads `this` again. Through `TurnNow` it also reads
   the delegate field `Turn`.
4. The `Chart` is young and reachable only from the closure (heap). When a minor GC runs inside `get_Summary`,
   nothing pins the `Chart`. The GC moves it and updates the closure's field, but `$0` still points at the old
   nursery address.

`mode=holder` is the control. The lambda `() => holder.C!.Summary` has two loads, so the intermediate is spilled to
the frame and pins the object. It does not crash. The only difference between the two modes is that spill.

## Results

| build | variant | result |
|---|---|---|
| full AOT, runtime 10.0.11, 10.0.12, 10.0.15 | `mode=build`, default GC | `NullReferenceException` **at round 19843, every run**, on two machines |
| full AOT | `MONO_GC_DEBUG=collect-before-allocs=1,clear-nursery-at-gc` | NRE at round 98 |
| full AOT | `MONO_GC_PARAMS=nursery-size=128m` (no minor GC) | OK, 100000 rounds |
| full AOT | `mode=holder` (the caller spills the load) | OK, 100000 rounds |
| interpreter (`-p:RunAOTCompilation=false`) | `mode=build` | OK, 30000 rounds (past 19843) |
| full AOT, runtime 11.0.0 | default GC and the oracle | does not reproduce, **but the generated code has the same unspilled shape** |

`MONO_GC_DEBUG=check-remset-consistency` reports nothing, alone and with the oracle. So this is not a missing write
barrier.

## Build and run

Requires the .NET 10 SDK, the `wasm-tools` workload, Node.js and Chrome.

```bash
dotnet workload install wasm-tools
dotnet publish AotGcRepro/AotGcRepro.csproj -c Release      # "AOT'ing 6 assemblies"
```

**In a browser.** Serve `AotGcRepro/bin/Release/net10.0/publish/wwwroot` with any static server and open:

- `/?mode=build&rounds=100000` crashes
- `/?mode=holder&rounds=100000` passes
- `/?mode=build&rounds=100000&params=nursery-size=128m` passes
- `/?mode=build&rounds=300&gc=collect-before-allocs=1,clear-nursery-at-gc` crashes early

The output appears on the page and in the console. Query parameters: `rounds`, `mode=build|holder|both`, `gc=` (sets
`MONO_GC_DEBUG`), `params=` (sets `MONO_GC_PARAMS`).

**Scripted** (headed Chrome through puppeteer-core):

```bash
cd tools && npm install && cd ..
node tools/run.mjs 100000 "mode=build" "mode=holder" "mode=build&params=nursery-size=128m"
node tools/run.mjs 300 "mode=build&gc=collect-before-allocs=1,clear-nursery-at-gc"
```

`CHROME` overrides the browser path (default `C:/Program Files/Google/Chrome/Application/chrome.exe`).

Example output:

```
=== mode=build (100000 rounds)
  REPRO build round 19843: NullReferenceException: Arg_NullReferenceException
  EXIT 2
=== mode=holder (100000 rounds)
  REPRO OK: 100000 rounds (holder), no wrong summary
  EXIT 0
```

## Looking at the code

`WasmEmitSymbolMap=true` writes `AotGcRepro/obj/Release/net10.0/wasm/for-publish/dotnet.native.js.symbols` in the
format `index:name`. To disassemble the module:

```bash
wasm-dis bin/.../_framework/dotnet.native.*.wasm -o r.wat
```

`wasm-dis` is in the Emscripten pack, under `packs/Microsoft.NET.Runtime.Emscripten.*.Sdk.*/<ver>/tools/bin`. The
`$N` names in the `.wat` count defined functions only, so `$N` = symbol index − the number of imported functions:

```bash
grep -c "^ (import.*func" r.wat
```

Look at `AotGcRepro_Chart__c__DisplayClass8_0__Buildb__0` and `AotGcRepro_Chart_get_Summary`.

## Notes

- The helpers `SetChart`, `MakeSaid` and `Scrub` exist so that no live or stale stack slot pins the `Chart` by
  accident. In the real app the object was made deep inside code that had already returned.
- The round number depends only on the allocation pattern. On two different machines and on three 10.0.x runtimes
  it was 19843 every time.
- The workaround in the app is to leave the affected assemblies out of AOT, so that they run in the interpreter.
