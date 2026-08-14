# Changelog

Newest first. One entry per work session; sections only where they add clarity.

## 2026-08-14 — Stream Deck integration via a loopback control API

### Added

- **`LocalApi`** (`SimLauncher.Core`): an `HttpListener` bound to `http://127.0.0.1:<port>/`
  exposing `/status`, `/start`, `/stop`, `/toggle`. Started from `App.OnStartup` after the
  coordinator initialises; disposed with the DI container. Loopback-specific prefixes need
  no URL ACL, so the app still runs unelevated. No auth by design — any local process can
  already start the same apps, so this is the same trust level as the tray icon; the
  off-switch is `localApiPort: 0`.
- **`localApiPort` config key** (default 8731). Hot-reload does *not* rebind the listener;
  changing the port needs a restart.
- **Stream Deck plugin** at `streamdeck/com.gareth.simlauncher.sdPlugin/`. Deliberately the
  HTML/JS plugin type: Stream Deck runs `plugin.html` directly, so there is no Node
  toolchain, no bundler, and no second build to keep in sync with the app. It polls
  `/status` every second and renders the key as an inline SVG data URI, so the whole status
  vocabulary (offline / off / booting / connected / armed / in-flight) lives in one
  `view()` function instead of a set of pre-baked PNG states. One key toggles the stack.
- `LocalApiTests` covers the routing and JSON shape end to end against a real listener.

### Notes

- `/stop?msfs=1` (and the plugin's `CLOSE_MSFS_ON_STOP` flag, default off) asks MSFS to
  close its main window after teardown. It never kills the sim: SimLauncher's standing rule
  is that it does not own the sim's lifetime, and a forced kill risks a corrupt sim state.
  With the flag off, pressing the key to shut down leaves MSFS running — intentional
  asymmetry with launch, flip the flag if you want symmetry.
- Requires a rebuild plus copying the plugin folder to
  `%APPDATA%\Elgato\StreamDeck\Plugins\` and restarting the Stream Deck app.

## 2026-08-06 — Remove traffic monitor; stop committing SimConnect DLLs

### Removed

- **BeyondATC traffic monitor deleted in full**: the `SimLauncher.Traffic` project
  (websocket client, state store, conflict detector, auto-culler, geo math), its
  test project, `TrafficWindow`, `TrafficViewModel`, the Leaflet map asset, and
  `docs/traffic-monitor.md`. The launcher is back to being only a checkpoint-driven
  app launcher.
- Verified nothing dangles: no traffic entries in `SimLauncher.slnx`, no
  `ProjectReference` from the app, no traffic keys in `Models.cs` /
  `ConfigValidator.cs` / `docs/config.sample.json`, no traffic UI in `MainWindow`,
  and `Assets/` is down to `app.ico`. Release build of the whole solution is clean
  and all **56 Core tests pass**. Remaining `BeyondATC` references are unrelated —
  that is a *managed app* the launcher starts, not the removed monitor.
- Stale comment in `App.xaml.cs` that justified the unhandled-exception logging by
  pointing at the (now deleted) traffic-window crash was reworded; the handler
  itself is unchanged and still wanted.

### Changed

- **Vendored SimConnect DLLs are no longer committed.** `.gitignore` no longer
  carves out `libs/SimConnect/*.dll`, so the two Microsoft SDK components stay
  local. This restores the README's existing claim that the SDK's DLLs are not in
  the repo, and makes the tree safe to push to a shared remote.
- `libs/SimConnect/README.md` (still committed) now leads with the fact that the
  DLLs are absent from a fresh clone and documents how to repopulate them.
  Consequence for a clean clone with no MSFS SDK installed: the build still
  succeeds but compiles the SimConnect **stub** — it warns at build time and sim
  state detection is disabled at runtime.
- `SingleFile` publish profile is functionally unchanged (still one self-contained
  `SimLauncher.exe`, ~69 MB, self-extracting to a per-version temp dir on first
  launch — the drop-in for the flight sim folder). Its comment now records why
  `IncludeAllContentForSelfExtract` is kept: dropping it unbundles WPF's five
  native DLLs as well as `SimConnect.dll`, giving a 7-file deploy rather than the
  2-file one it might suggest.

## 2026-08-06 — Fix 1.3 GB native memory leak in SimConnect polling

### Fixed

- **Memory leak: every failed SimConnect connect attempt stranded ~600 KB of native
  memory.** The app grew to 1.3 GB while sitting idle in the tray.

  Root cause, from a heap dump of the 1.78 GB process: the managed heap was only
  **38 MB**, so the growth was entirely native. `SimConnectStateSource.TryConnect()`
  runs every `pollIntervalSeconds` (default 5 s) forever while MSFS is down, and the
  MSFS SDK's `SimConnect` constructor allocates its native connection state *before*
  `SimConnect_Open` fails and throws `COMException` (E_FAIL). The half-constructed
  object therefore never reaches our `catch` — it is unreachable and undisposable, and
  only its finalizer can release the native side. Because its managed husk is just
  360 bytes, the GC saw no pressure and never ran, so the finalizer queue was never
  drained. The dump showed 2,190 `SimConnect` objects with **zero GC roots**, 815 of
  them still awaiting finalization, plus 2,194 `SafeWaitHandle`s — one per attempt,
  roughly 3 hours of idle polling.

  Fix, in `TryConnect()`:
  - The `EventWaitHandle` created for each attempt is now disposed when the
    constructor throws (previously leaked 1:1 with attempts — that one was ours).
  - Every 10th consecutive failure forces `GC.Collect()` +
    `GC.WaitForPendingFinalizers()`, which is the only way to release a reference we
    never receive. Draining every 10th attempt rather than every attempt keeps the
    collections rare (~1/min at the default poll interval) while capping the stranded
    native memory at a few MB. The counter resets once a connection succeeds.

### Caveats

- The forced collections only run while the sim is down and a connect attempt has just
  failed; they never run during an active session.
- Not changed: `TryConnect()` still attempts a connection regardless of whether an MSFS
  process exists. Gating on `SessionCoordinator.IsMsfsProcessRunning()` would remove
  nearly all failed attempts, but it would make sim detection depend on
  `msfs.processNames` being correct, so it was left alone — the drain bounds the cost
  either way.
- Unrelated minor leak left in place: `WindowsProcessManager.FindExisting()` returns
  early without disposing the remaining `Process` objects from
  `Process.GetProcessesByName()`. A handful of handles per launch, not a growth source.

### Action required

- Rebuild and restart SimLauncher for the fix to take effect.

## 2026-07-18 — Single-file self-contained publish profile

### Added

- **`SingleFile` publish profile** (`src/SimLauncher.App/Properties/PublishProfiles/SingleFile.pubxml`).
  `dotnet publish src/SimLauncher.App -c Release -p:PublishProfile=SingleFile`
  now produces one self-contained ~69 MB `SimLauncher.exe` in
  `src/SimLauncher.App/bin/publish/` — no loose .NET assemblies, no runtime
  install needed on the target machine.
- Details: `IncludeAllContentForSelfExtract` is used (not just native-libs
  extraction) because the native `SimConnect.dll` enters the build as a plain
  content item from `SimConnectSdk.props`, so it would otherwise publish as a
  loose file. The bundle self-extracts to a per-version temp dir on first
  launch (slightly slower first start only). A `StripPdbsFromPublish` target
  removes referenced projects' `.pdb`s, which `DebugType=none` alone doesn't
  cover. Trimming is off — WPF doesn't support it. Normal F5/`dotnet build`
  output is unchanged.
- **Vendored SimConnect DLLs** (`libs/SimConnect/`): `SimConnect.dll` and
  `Microsoft.FlightSimulator.SimConnect.dll` copied from the MSFS 2024 SDK so
  builds/publishes no longer require the SDK (or its env vars) on the machine.
  `SimConnectSdk.props` still prefers `-p:SimConnectSdkDir` / `%MSFS2024_SDK%`
  / `%MSFS_SDK%` when present and falls back to the vendored copies otherwise;
  verified with a clean publish with both env vars cleared. `.gitignore`'s
  blanket SimConnect-DLL block now carves out `libs/SimConnect/` — keep the
  repo private / check the MSFS SDK EULA before distributing it. See
  `libs/SimConnect/README.md` for the update procedure (re-copy after SDK
  updates; the DLLs carry no version info, so the README records the copy
  date).

## 2026-07-17 — Map: player threshold rings + cull scoring/cones

### Added

- **Player caution & conflict rings on the map.** Two dashed circles centred
  on the player showing the horizontal conflict (red) and caution (yellow)
  thresholds, each labelled along the ring with its radius and vertical band
  (e.g. `3 nm · ±1000 ft`). Radii come from the live config thresholds, so
  Apply in the thresholds panel updates the rings on the next snapshot. Hidden
  while the player is on the ground (ground aircraft are excluded from
  detection, so rings there would be misleading).
- **Cull scoring & forward cones for CONFLICT pairs.** For every pair at
  CONFLICT severity the map now draws each aircraft's ±70° forward cone (the
  `CullPolicy` "behind" test, radius scaled to the pair separation); a cone
  lights up orange when the other aircraft sits inside it, i.e. its owner is
  the chaser scoring +2. Each conflicted aircraft's label pill gains a
  `score N` line, with a `· cull` suffix marking the aircraft `CullPolicy`
  would remove; the click popup shows the same. Caution pairs are deliberately
  unscored — scoring only applies where the auto-culler could act.

### Changed

- `CullPolicy`: new `Assess(pair)` returning per-side intruder scores, chasing
  flags and the selected target (`PairAssessment` record); `BehindConeDeg`
  made public so the map cone matches the policy. `SelectTarget` behaviour
  unchanged.
- Snapshot JSON to the map page now carries `rings`, `coneDeg`, per-conflict
  scoring fields and per-aircraft `score`/`cull`; `BuildSnapshotJson` became
  an instance method to read thresholds from config.

No rebuild caveats beyond the usual: rebuild + restart the app to pick up
map.html.

## 2026-07-16 — Traffic window crash fix (action-log auto-scroll)

### Fixed

- **App crash when detection events arrived in quick succession.** The action-log
  auto-scroll called `ListBox.ScrollIntoView` synchronously from inside the
  `ObservableCollection.CollectionChanged` dispatch. `ScrollIntoView` forces a full
  layout pass mid-notification, which races WPF's `ItemContainerGenerator` and throws
  `InvalidOperationException: An ItemsControl is inconsistent with its items source`
  (unhandled → process death). Latent since the window was written; exposed on
  2026-07-16 when the new DET lifecycle mirroring produced two action-log
  entries in the same feed tick (WER stack trace: `TrafficWindow.<.ctor>b__6_2`
  → `ScrollIntoView` → `VirtualizingStackPanel.MeasureChild` → `Verify`). The scroll
  is now deferred via `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)`.
- **Unhandled exceptions are now logged before the process dies.** The crash above
  left no trace in our own log — only Windows Event Viewer had it. `App.OnStartup`
  now hooks `DispatcherUnhandledException` and `AppDomain.UnhandledException` and
  writes a Fatal entry via Serilog. Exceptions are still fatal (log-and-die, not
  swallow).

### User action

- Rebuild required. The build after the fix could not replace `SimLauncher.exe`
  because the app was running — close it and `dotnet build` (compile itself passed).

## 2026-07-15 — Conflict detection fix (missed short-final conflict) + detection diagnostics

### Fixed

- **`atDestination` no longer excludes aircraft from conflict detection.** BATC sets
  the flag on arrivals that are still airborne (short final, go-arounds), so the old
  exclusion silently blinded the detector to exactly the reported scenario: player
  and AI both on short final, the one ahead sent around, no alert. A landed aircraft
  is already excluded by `onGround`, so the flag added nothing but the blind spot.
  Regression test: `AirborneAtDestinationTrafficIsStillDetected`. Auto-cull is
  unchanged — it still refuses `atDestination` targets; a go-around aircraft can be
  removed manually via its map marker popup.
- Range history now starts at the diagnostic envelope (2× caution thresholds), so a
  pair closing into the caution band is flagged on the tick it crosses the threshold
  instead of one tick late.

### Added

- **Structured detection log** `%APPDATA%\SimLauncher\logs\traffic-<date>.clef`
  (Serilog CLEF / newline-delimited JSON, rolled daily, 7 days kept). Every event
  carries UTC `@t` plus the feed's `SimTime` for cross-correlation with BeyondATC's
  own logs. Contents: per-tick summary (counts, scope, full player state), a
  near-pair trace with the gate that decided each pair
  (`Flagged`/`FirstSighting`/`NotClosing`/`VerticalSeparation`/`HorizontalSeparation`),
  edge-triggered eligibility transitions with `ExclusionReasons`, conflict lifecycle
  (begin/escalate/downgrade/end with duration), and player warnings (missing from
  feed / excluded from detection).
- Detection events in the traffic panel's action log with a purple **DET** badge
  (lifecycle + player warnings only; the per-tick trace stays file-only).
- Tests for the previously untested `PlayerVsAi` (default) and `AiVsAi` scopes and
  for gate attribution. Suite: 47 passing.

### Changed

- `ConflictDetector.Evaluate` returns `ConflictEvaluation` (conflicts + near-pair
  diagnostics + exclusions) instead of a bare conflict list.
- `TrafficMonitorService` takes `ILoggerFactory` (needs the dedicated
  `SimLauncher.Traffic.Detection` logger).
- New package: `Serilog.Formatting.Compact` (App).
- Docs: `conflictScope` config row, `atDestination` rationale, and a "Detection
  diagnostics & logging" section in `docs/traffic-monitor.md`; README points at the
  new log file.
