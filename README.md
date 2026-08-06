# SimLauncher

A Windows tray application that starts and stops MSFS companion apps (SPAD.neXt,
BeyondATC, REX Atmos Core, AutoFPS, …) at defined checkpoints in a Microsoft Flight
Simulator 2024 session, driven by SimConnect state detection.

The point: ordering. REX must initialise **after** SimConnect is up but **before** the
flight loads; AutoFPS only makes sense once you're in the world; everything should be
shut down cleanly when the sim exits. SimLauncher's own exit never takes managed
apps with it — they are orphaned and keep running.

## The checkpoint timeline

| Checkpoint | Trigger |
|-|-|
| On Session Start | A session begins — either you click **Launch MSFS** (which also starts the sim) or a running sim is detected and the session auto-arms. Apps here start "alongside" MSFS |
| On Sim Start | A SimConnect connection has been accepted |
| On World Load / Free Flight Start | `FlightLoaded` system event, gated on CAMERA STATE having left the menu/loading values |
| On Enter Cockpit | CAMERA STATE enters the cockpit range (you clicked *Ready to Fly*) |
| On Exit Flight | CAMERA STATE returns to the main menu while the connection stays alive |
| On Sim Exit | `Quit` system event, or connection loss not recovered within the grace period (default 30 s) → teardown |

On Exit Flight is re-entrant (fly → menu → fly again): World Load and Enter Cockpit
re-arm for the next flight; apps still running are not relaunched. On Sim Start and
On Sim Exit fire once per session. All camera transitions are debounced (default 2 s).

**MSFS is not a managed app.** The `msfs` config block tells the Launch button how to
start the sim (MS Store shell URI by default; Steam: `steam://rungameid/2537590`).
SimLauncher never shuts the sim down. SimConnect
monitoring runs permanently: if MSFS is already running (or starts on its own), the
button disables and a session arms automatically as soon as the connection is accepted —
attaching mid-flight catches the timeline up (World Load / Enter Cockpit fire from the
current camera state). Set `autoStartSessionWhenSimDetected: false` to opt out. After a
manual Stop, auto-arm stays suppressed until the sim exits, so it won't fight you.

## Projects

| Project | Contents |
|-|-|
| `SimLauncher.Core` | State machine, checkpoint engine, process management, config. No WPF, no SimConnect. Everything arrives via `ISimStateSource` / `IProcessManager`, so it is fully unit-testable |
| `SimLauncher.SimConnect` | `ISimStateSource` implementation over `Microsoft.FlightSimulator.SimConnect` |
| `SimLauncher.App` | WPF tray app + timeline UI, thin layer over Core |
| `SimLauncher.Core.Tests` | xUnit suite driving Core with fakes and a fake clock |

## Building

```
dotnet build
dotnet test
```

Requires .NET 8 SDK (or newer) on Windows. Without the MSFS SDK the solution still
builds, but `SimLauncher.SimConnect` compiles a **stub** (a build warning says so) and
the app cannot detect sim state — install the SDK and rebuild for real use.

### SimConnect SDK setup

The MSFS SDK is not redistributable, so its DLLs are not in this repo.

1. Install the MSFS SDK: in MSFS 2024 enable Developer Mode
   (*Options → General → Developers*), then *Help → SDK Installer*, or download it from
   [docs.flightsimulator.com](https://docs.flightsimulator.com/).
2. The installer sets the `MSFS2024_SDK` (or `MSFS_SDK` for 2020) environment variable,
   typically `C:\MSFS 2024 SDK\`. The build picks up, in order:
   `-p:SimConnectSdkDir=<path>`, then `%MSFS2024_SDK%`, then `%MSFS_SDK%`.
3. Two files are used from `<SDK>\SimConnect SDK\lib\`:
   - `managed\Microsoft.FlightSimulator.SimConnect.dll` — the managed wrapper,
     referenced at compile time;
   - `SimConnect.dll` — the native x64 library, copied next to the exe at build time
     (the managed wrapper P/Invokes it).
4. Rebuild. The "Building SimConnect STUB" warning must be gone.

The app is built x64 to match the native DLL.

## Configuration

Single JSON file at `%APPDATA%\SimLauncher\config.json`. Created with a sample setup on
first run (see `docs/config.sample.json`). Hot-reloaded when edited externally; an
invalid edit keeps the last good config and shows the errors in a banner instead of
crashing. Multiple named profiles are supported; switch via the tray menu or the
selector in the window (not while a session is active).

### Schema

Top level:

| Key | Meaning |
|-|-|
| `activeProfile` | Name of the profile in use |
| `autoStartSessionWhenSimDetected` | Auto-arm a session when a running sim is detected (default true) |
| `msfs.path` | How the Launch button starts the sim: exe path, MS Store shell URI, or steam:// command |
| `msfs.processNames` | Process names used to detect a running sim (default `FlightSimulator2024`, `FlightSimulator`) |
| `simConnection.pollIntervalSeconds` | SimConnect connection poll interval (default 5) |
| `simConnection.disconnectGraceSeconds` | Reconnect window before a drop counts as sim exit (default 30) |
| `simConnection.debounceSeconds` | State-transition debounce (default 2) |
| `simConnection.cameraStates` | CAMERA STATE value map — see verification below |
| `profiles[]` | Named profiles, each with `name` and `apps[]` |

Per app:

| Key | Meaning |
|-|-|
| `name` | Display name; must be unique within the profile |
| `path` | Exe path, or URI/steam command (`steam://rungameid/2537590`, `shell:AppsFolder\…`) — URIs launch via the shell automatically |
| `args` | Command-line arguments |
| `checkpoint` | `launcherStart`, `onSimStart`, `onWorldLoad`, `onEnterCockpit`, `onExitFlight`, `onSimExit` |
| `delaySeconds` | Launch N seconds after the checkpoint (canonical unit is seconds; the UI offers a seconds/minutes selector). Countdowns are cancelled by teardown or Exit Flight |
| `waitForApp` | Name of another app at the **same** checkpoint that must start first |
| `waitForAppReadySeconds` | Extra wait after `waitForApp` starts |
| `shutdown` | `graceful` (CloseMainWindow → wait → kill), `kill`, `leave` (never touched). Omitted = graceful, or leave for adopted processes |
| `shutdownTimeoutSeconds` | Graceful wait before killing (default 10) |
| `restartIfCrashed` | Relaunch on unexpected exit, max 3 attempts with backoff |
| `alreadyRunning` | `skip` (default), `adopt` (manage the existing instance), `startAnother` |
| `shellExecute` | Force ShellExecute-style start (auto for URIs) |
| `runAsAdmin` | Launch elevated via a UAC prompt, for apps whose manifest demands administrator (e.g. REX Atmos Core). Launches that fail with ERROR_ELEVATION_REQUIRED retry elevated automatically |

Elevation caveat: unless SimLauncher itself runs as administrator, Windows blocks a
non-elevated process from closing or killing an elevated one — so an
elevated app effectively behaves as `leave` on teardown, and each launch shows a UAC
prompt. Run SimLauncher elevated if you want full lifecycle control over elevated apps
(children inherit elevation, so the prompt disappears too).

Launcher exit: managed apps are never tied to SimLauncher's own lifetime. If
SimLauncher exits (or crashes), launched apps are orphaned and keep running — the
only thing that ever shuts an app down is the session teardown flow, per its
`shutdown` mode. Adopted processes are left alone on teardown unless `shutdown`
is set explicitly.

## Verifying CAMERA STATE values in MSFS 2024

The camera-state map ships with the documented MSFS 2020 values (11 = main menu,
12 = loading screen, 2–6 = cockpit, 2–10 = in flight). **Verify them in MSFS 2024
before trusting the timeline:**

1. Start MSFS manually, open SimLauncher's window and expand the **Debug** panel.
2. Click **Launch MSFS** (MSFS is already running — it will be skipped) so the state
   machine connects. The panel shows the live CAMERA STATE value, connection state and
   the last five system events.
3. Note the value in the main menu, during loading, after the world loads, and after
   clicking *Ready to Fly*.
4. If they differ from the defaults, edit `simConnection.cameraStates` in
   `config.json` accordingly (hot-reloads immediately).

Known quirk this design works around: MSFS sometimes fires `FlightLoaded` during menu
transitions, so World Load additionally requires the camera to have left the
menu/loading values.

## Logs

Serilog rolling files in `%APPDATA%\SimLauncher\logs\` (14 days kept), plus the live
session log in the Debug panel. Every state transition, checkpoint fire and process
start/stop is logged.

## Smoke test without flying

Select the **Test (Notepad)** profile and click Launch MSFS: notepad instances launch
at On Sim Start / On World Load / On Enter Cockpit as you progress into a flight, and
are closed on teardown. MSFS itself is `leave` and survives.

## Non-goals (v1)

No node-graph editor, no LVAR/WASM state sources, no window-title or log-file
watching, no per-aircraft profiles. `ISimStateSource` and the checkpoint model are the
extension points for adding those without reworking Core.
