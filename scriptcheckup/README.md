# ScriptCheckup — Roslyn-based Unity C# analyzer

Static analysis for Unity C# scripts built on **Roslyn** (`Microsoft.CodeAnalysis` 5.9.0):
syntax + semantic model + `ControlFlowGraph` + `SemanticModel.AnalyzeDataFlow`.

**46 rules**: 11 RB (readback) · 11 UW (Update waste) · 10 UL (lint) · 7 RX (reinit) · 2 UE (errors) · 5 DF (dataflow).

## Rules

### Readback rules (RB) — GPU↔CPU synchronization

| ID | Sev | What it finds |
|----|-----|---------------|
| RB001 | warning | `Texture2D.ReadPixels` in `Update`/`FixedUpdate`/`LateUpdate` (GPU→CPU sync point). |
| RB002 | warning | `RenderTexture.active` assigned with no restore at all (syntax-based; DF004 is the path-sensitive version). |
| RB003 | warning | `AsyncGPUReadback.Request` without checking `request.hasError` before reading data. |
| RB004 | warning/info | `AsyncGPUReadback.WaitAllRequests` outside teardown (`OnDisable`/`OnDestroy`/`OnApplicationQuit`) — same stall as a sync readback. Info inside teardown. |
| RB005 | warning | `GetTemporary` without *any* `ReleaseTemporary` in the method (syntax-based; DF003 is the path-sensitive version). |
| RB006 | warning | `ComputeBuffer`/`GraphicsBuffer.GetData` — synchronous GPU read, CPU blocks. |
| RB007 | info | `Texture2D.Apply()` without arguments — sync upload, keeps CPU copy in memory. |
| RB008 | warning | `ScreenCapture.CaptureScreenshot` — full pipeline stall + screen buffer. |
| RB009 | warning/info | `Texture2D.GetPixels`/`GetPixels32` — expensive CPU copy read, usually follows a readback. |
| RB010 | warning/info | Writing to `Camera.targetTexture` — don't read the RT in the same frame (warning in hot path). |
| RB011 | info | `GetRawTextureData<T>()` — cheap but layout/format is platform-dependent. |

### Update-waste rules (UW) — per-frame costs

| ID | Sev | What it finds |
|----|-----|---------------|
| UW001 | warning | `FindObjectOfType`/`FindObjectsOfType`/`FindAnyObjectByType` (incl. generic) in hot methods. |
| UW002 | warning | `GameObject.Find` / `FindWithTag` / `FindGameObjectsWithTag` in `Update`/`FixedUpdate`/`LateUpdate`. |
| UW003 | warning | `Camera.main` in hot methods — tag search every call. |
| UW004 | warning | Physics in `Update`/`LateUpdate`: `AddForce`, `velocity`/`linearVelocity`/`angularVelocity` assignment, `MovePosition`/`MoveRotation`. `FixedUpdate` is the right place. One-shot `AddForce(x, ForceMode.Impulse)` is not flagged. |
| UW005 | warning | Movement/rotation (`Translate`, `Rotate`, `position +=` …) without `Time.deltaTime`/`fixedDeltaTime`. |
| UW006 | warning | `gameObject.tag == "..."` — use `CompareTag`. |
| UW007 | warning | Event subscription (`+=`) without matching `-=` in `OnDisable`/`OnDestroy`/`OnApplicationQuit`. Handles `obj.Event += H` and bare `Event += H` (event-typed fields only — plain `score += 10` is silent). |
| UW008 | warning | `async void` — unobserved exceptions crash the process; use `async Task`. |
| UW009 | warning | `Resources.Load` in hot methods — disk load every frame. |
| UW010 | warning | `FindObjectOfType` outside hot methods — still cache in `Awake`/`Start`. |
| UW011 | warning | `GetComponent` in hot methods — cache in `Awake`/`Start`. |

### Lint rules (UL) — style and API hygiene

| ID | Sev | What it finds |
|----|-----|---------------|
| UL001 | info | Public field in `MonoBehaviour` — breaks encapsulation, prefer `[SerializeField] private`. |
| UL002 | info | `Debug.Log`/`LogWarning`/`LogError` — remove before build. |
| UL003 | info | `StartCoroutine("Name")` — string-based, typos invisible to compiler. |
| UL004 | info | `Invoke("Name", …)` — string-based, refactoring breaks silently. |
| UL005 | info | `SendMessage` — slow and untyped; prefer events/direct calls. |
| UL006 | info | Heap allocation (`new List<>`, `new string`, arrays, …) in hot methods — GC pressure. Value types (`Vector3`, `Quaternion`, `Color`) are not flagged: they don't allocate. |
| UL007 | info | String concatenation (`+`/`$""`) in hot methods — garbage every frame. |
| UL008 | info | Empty `Start()`/`Update()` etc. — Unity still invokes them. |
| UL009 | info | Class not inheriting `MonoBehaviour` — cannot be attached to a GameObject. |
| UL011 | info | `GameObject.Find*` outside `Update` — find once, keep the reference. |

### Reinit rules (RX) — domain/scene reload survival

| ID | Sev | What it finds |
|----|-----|---------------|
| RX001 | warning | `DestroyImmediate` outside editor scripts — use `Object.Destroy` at runtime. |
| RX002 | warning | Static singleton assigned in `Awake` but never nulled — with Domain Reload disabled it points at a destroyed object. |
| RX003 | warning | `static event` subscribed but never unsubscribed — handler list grows across Play Mode sessions. |
| RX004 | info | Static counter incremented but never reset — keeps counting from the previous session. |
| RX005 | warning | `DontDestroyOnLoad` without a duplicate guard (`if (Instance != null && Instance != this) Destroy(gameObject)`). |
| RX006 | info | `UnityEditor` API correctly isolated inside `#if UNITY_EDITOR` (positive note — don't forget `#endif`). |
| RX008 | warning | `SceneManager.sceneLoaded` subscribed without unsubscription — duplicates after every scene reload. |

### Error rules (UE)

| ID | Sev | What it finds |
|----|-----|---------------|
| UE001 | error | Unbalanced curly braces in the file. |
| UE003 | error | `Thread.Sleep` — blocks Unity's main thread, the game freezes. |

### Dataflow rules (DF) — path-sensitive, CFG-based

| ID | Sev | What it finds |
|----|-----|---------------|
| DF001 | warning | Local assigned but never read (`SemanticModel.AnalyzeDataFlow`). |
| DF002 | warning | Dead store: value overwritten or out of scope before any read (forward CFG walk). Skips `out`/`ref` writes observed by the caller. |
| DF003 | warning | `RenderTexture.GetTemporary` lease not released with `ReleaseTemporary` on **all** CFG paths to method exit. Path-sensitive successor of RB005. Also fires on discarded leases (`GetTemporary(...);` as a statement). Has a code fix (wraps the post-acquisition code in `try`/`finally` with the release in `finally`; pre-existing releases of the same lease are folded into it). |
| DF004 | warning | `RenderTexture.active` assigned but a restore is bypassed on some exit path (forward may-analysis). Fires when a restore exists but an early return can skip it; the no-restore-at-all case is RB002. Has a code fix (wraps the body in `try`/`finally` with the restore in `finally`; works with the save in nested blocks too). |
| DF005 | warning | Dereference of a definitely-null local/parameter (branch-refined null-state analysis over the CFG). |

## DF003 vs RB005 · DF004 vs RB002

RB005 is cheap and syntax-based: it only checks that a `ReleaseTemporary` call exists
*somewhere* in the method. DF003 walks the CFG and verifies that **every** path from the
`GetTemporary` lease to a method exit passes through a release of that lease. A method can
pass RB005 and still fail DF003 (release on one branch only).

RB002 fires when `RenderTexture.active` is assigned with **no** restore anywhere in the
method. DF004 fires when a restore exists but an early `return`/`throw` can bypass it.

## Build & run

```bash
dotnet build
dotnet ScriptCheckup.Cli/bin/Debug/net8.0/ScriptCheckup.Cli.dll --dir samples
dotnet ... --dir Assets --recursive --sarif out.sarif --disable UL008
# Point at your Unity install for exact symbol resolution:
dotnet ... --dir Assets -R --reference /opt/unity/Editor/Data/Managed/UnityEngine.dll
```

Tests:

```bash
dotnet test ScriptCheckup.Tests
```

## Changelog

### v2.4.1
- Fail-closed hardened (round 4): `AD*` analyzer crashes are single-counted, print
  as `error`, map to `error` in SARIF with `executionSuccessful: false` and
  `toolExecutionNotifications`; distinct exit code **3** for infrastructure failure
  (vs 2 for rule violations, 1 for warnings, 0 for clean).
- New synthetic gate rule **UE000**: files with syntax errors are blocking results.
- Zero analyzed files is exit 3, not 1.
- SARIF: `tool.driver.version` from the CLI assembly (2.4.1, was hardcoded
  `2.3.0-roslyn`); relative artifact URIs with `uriBaseId: SRCROOT`; results sorted
  by (uri, line, column, rule); `partialFingerprints.scriptcheckup/v1` on every
  result (rule id + normalized source line + enclosing symbol) for waivers.
- DF003: release inside a capturing lambda/local function (e.g. the
  `AsyncGPUReadback` callback RB003 recommends) is a deferred release, not a leak;
  `is null` / `is not null` guards recognized; `return rt == null;` no longer
  misclassified as ownership escape.
- DF003 code fix: releases inside blocks/switch sections are removed outright (no
  more `{ ; return; }`); a brace-less `if` with a side-effecting condition keeps
  its condition with an empty body.
- Verdict changes since v2.3 (ruleset): UL006 no longer flags `Vector3`/
  `Quaternion`/`Color` (heap/reference types only); UL009 excludes
  `ScriptableObject` and abstract classes; UW004 silent on
  `ForceMode.Impulse`/`VelocityChange`, understands `linearVelocity`; UL001 scoped
  to Unity base types instead of every C# class; UL007 handles interpolated
  strings; `FindAnyObjectByType` covered by UW001/UW010.

### v2.4
- Roslyn unified to 5.9.0 across analyzers, CLI and tests (was 4.8.0 vs 5.9.0).
- Fail-closed: `AD*` analyzer diagnostics are always blocking, never filtered by `--disable`.
- DF003: null-check pruning now reads `BasicBlock.ConditionKind` — no more pruned live
  branches for `if (rt == null || flag)`-style short-circuits.
- DF003 code fix: a release embedded in a brace-less `if` is replaced with `;` instead
  of being deleted (keeps the tree valid).
- DF002: assignments under `if`/`?:`/`switch`/loops no longer count as unconditional
  overwrites.
- UE001: stray braces hidden in parser skipped-trivia are now counted.

## Limitations

- **No `UnityEngine.dll` reference by default** → Unity APIs are matched by name
  (`RenderTexture`, `GetTemporary`, …). Pass `--reference <UnityEngine.dll>` (from
  `Editor/Data/Managed/` of your Unity install) for exact symbol resolution.
  The DF003/DF004/DF005 dataflow rules degrade gracefully: when a Unity call does not
  bind, Roslyn surfaces it as an `Invalid` operation and the rules fall back to
  syntax-shape matching, so they keep working without the reference.
- Scripts are parsed with `UNITY_EDITOR` defined, so `#if UNITY_EDITOR` blocks are
  analyzed as active code (otherwise Roslyn treats them as skipped trivia).
  Known blind spot (round 4, 2.4): *any* branch on platform symbols is skipped —
  `#if UNITY_ANDROID`, `#if !UNITY_EDITOR`, `#if UNITY_XR`, and the `#else` side of
  `#if UNITY_EDITOR`. For Android XR client code that is the code running on the
  device. Multi-pass analysis over symbol sets (editor + device) is planned; until
  then treat a clean run as "clean under `UNITY_EDITOR`".
- **Fail-closed:** an analyzer crash surfaces as `AD0001` (Roslyn reports it as a
  warning). The CLI always counts `AD*` diagnostics as blocking errors, never
  filters them out via `--disable`, marks the SARIF run unsuccessful, and exits
  with code **3** (infrastructure failure — distinct from 2 = rule violations).
  A file that does not parse is reported as **UE000** (blocking). Zero analyzed
  files is exit 3. The bundled workflow no longer swallows the exit code, so a
  broken analyzer run cannot silently pass *through the CLI*; an external policy
  layer still decides what blocks the gate.
- **Implicit exceptional edges are not modelled.** Roslyn's CFG has no edges for
  exceptions thrown mid-method, so a leak/restore that only happens via an exception is
  a false *negative*, never a false positive.
- **`finally` regions are orphaned in Roslyn's CFG** (normal exits step straight to the
  exit block). DF002/DF003/DF004 compensate syntactically for the two idiomatic patterns:
  the operation is inside the try-block, or linearly precedes the try in the same
  statement block with no `return`/`throw`/`goto`/`break`/`continue` in between, and the
  finally performs the release/restore/read. Exotic shapes may still warn.
- **Ownership escape:** a lease that is returned to the caller (or stored where the
  analysis cannot follow) is assumed transferred; DF003 does not warn.
- **Inter-procedural analysis is approximate** (one method body at a time).
