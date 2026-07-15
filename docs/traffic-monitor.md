# Traffic Monitor (BeyondATC live map + conflict cull)

Opened from the **Traffic Monitor** button in the main window. Connects to BeyondATC's
local traffic WebSocket (`ws://127.0.0.1:41717/`), draws every aircraft on a Mapbox map,
runs a pairwise 3D separation check the BATC sequencer doesn't do, and can despawn a
conflicting AI aircraft via BeyondATC's own `remove-aircraft` command. No BeyondATC
files are modified and nothing is injected — it only speaks the WebSocket protocol that
BATC's bundled traffic map already uses.

The server only listens while BeyondATC is in an active flight with traffic. The link
retries every ~2.5 s forever; the status bar shows connected/disconnected. It runs
independently of session management and never blocks launcher startup.

## Configuration (`%APPDATA%\SimLauncher\config.json`, `traffic` block)

| Key | Default | Meaning |
|-|-|-|
| `mapboxToken` | (a working pk token) | Mapbox public token. Empty string → falls back to the `MAPBOX_TOKEN` env var |
| `webSocketUrl` | `ws://127.0.0.1:41717/` | BATC traffic WebSocket |
| `conflictHorizontalNm` | `3.0` | CONFLICT when closer than this **and** below `conflictVerticalFt`, while closing |
| `conflictVerticalFt` | `1000` | |
| `cautionHorizontalNm` | `5.0` | CAUTION band (same rule, wider) |
| `cautionVerticalFt` | `1500` | |
| `conflictScope` | `playerVsAi` | Which pairs are checked: `playerVsAi`, `all`, or `aiVsAi` |
| `autoCull` | `false` | Automatically remove the intruder of a sustained CONFLICT |
| `dryRun` | `true` | Auto-cull only logs `WOULD remove …` instead of sending |
| `autoCullSustainSeconds` | `2` | CONFLICT must persist this long before auto-cull acts |
| `autoCullCooldownSeconds` | `120` | Never re-target the same callsign within this window |

Thresholds and the auto-cull/dry-run toggles are also editable live in the panel's
**Settings** expander; changes are saved back to the config file.

## Conflict detection

Every `aircraft-update` tick, all airborne `inSim` pairs allowed by `conflictScope`
are checked: great-circle horizontal distance, `abs(altA - altB)` vertical, and
closure (range must be shrinking vs. the previous tick — steady or opening pairs are
never flagged). Range history starts at the *diagnostic envelope* (2× the caution
thresholds), so a pair that closes into the caution band is flagged the moment it
crosses the threshold. Flagged pairs get a dashed line on the map, coloured
markers/labels, and a row in the Conflicts panel with distance / vertical / closure
rate and a "you" tag when the player is involved.

`atDestination` deliberately does **not** exclude an aircraft from detection: BATC
sets it on arrivals that are still airborne (short final, go-arounds), and a landed
aircraft is excluded by `onGround` anyway. It still makes an aircraft ineligible as
an auto-cull target.

The default scope is `playerVsAi`: only pairs involving your aircraft are checked.
Set `conflictScope` to `all` (or `aiVsAi`) — in config or the Settings expander — to
watch AI-vs-AI separation too.

Colour code everywhere (map, labels, lines, panel): **red** conflict involving you,
**yellow** caution involving you, **blue** AI-vs-AI conflict, **dim blue** AI-vs-AI
caution (the blue pair colours only appear under `all`/`aiVsAi` scope). Red only
ever means *you*.

Protocol note: inbound removal notices are accepted under both `remove-aircraft` and
`aircraft-remove` (the live feed uses the latter); the outbound command is always
`remove-aircraft`.

## Removing an aircraft

* **Manual** — the Remove button on a conflict row (targets the policy-chosen intruder)
  or in a marker popup. Always asks for confirmation. Removal is destructive and
  visible: the aircraft disappears from TCAS and scenery.
* **Auto-cull** (off by default) — acts on a CONFLICT sustained ≥ 2 s. It never targets
  the player, only airborne AI, prefers the trailing and/or higher aircraft of the pair
  (never the one about to land ahead), debounces per pair and per callsign, and refuses
  to fire if the target has left the feed.
* **Dry-run** (on by default) — while enabled, auto-cull writes
  `WOULD remove <callsign> (…)` to the action log instead of sending. Validate a few
  real flights before turning it off.

Fail-safe: any unrecognised feed message is logged raw and disarms auto-cull until the
next reconnect. Manual removals still work (they re-verify the callsign against the
live feed at send time). The player aircraft is never a valid target anywhere.

## Detection diagnostics & logging

Every detection decision is explainable after the fact from
`%APPDATA%\SimLauncher\logs\traffic-<date>.clef` — a structured newline-delimited
JSON file ([CLEF](https://clef-json.org/), rolled daily, 7 days kept) written by a
dedicated `SimLauncher.Traffic.Detection` logger. Each event carries the UTC
timestamp (`@t`) **and** the feed's `SimTime`, so entries can be cross-correlated
with BeyondATC's own logs. What's in it:

* **Tick summary** (every `aircraft-update`): aircraft/eligible/near-pair/flagged
  counts, active scope, and the player's full state (`inSim`, `onGround`,
  `atDestination`, altitude, `state`).
* **Near-pair trace**: every pair inside the diagnostic envelope gets one event per
  tick with distances, closure, and the gate that decided it —
  `Flagged` / `FirstSighting` / `NotClosing` / `VerticalSeparation` /
  `HorizontalSeparation` — plus both aircraft's flags. A silent miss always shows up
  here with its reason.
* **Eligibility transitions** (edge-triggered): an aircraft becoming excluded or
  eligible, with the `ExclusionReasons` flags and the raw feed fields.
* **Conflict lifecycle**: begin / escalate / downgrade / end (with duration and why
  it ended), at Information level, so it also lands in the main log.
* **Player warnings**: no `isPlayer` aircraft in the feed, or the player excluded
  from detection — the two states in which player-vs-AI detection is silently idle.

Lifecycle events and player warnings are mirrored into the panel's action log with a
**DET** badge; the per-tick trace stays file-only to keep the panel readable. Read
the file with any CLEF tool (e.g. `clef-tool`, Seq) or `jq` over the JSON lines.

## Code layout

| Piece | Where |
|-|-|
| WS transport (connect/reconnect/send) | `SimLauncher.Traffic/TrafficWebSocketClient.cs` |
| Protocol parsing (defensive, exact field names) | `SimLauncher.Traffic/TrafficMessage.cs` |
| Aircraft state store (upsert by callsign) | `SimLauncher.Traffic/TrafficStateStore.cs` |
| Conflict math | `SimLauncher.Traffic/ConflictDetector.cs`, `GeoMath.cs` |
| Cull policy + debounce | `SimLauncher.Traffic/CullPolicy.cs`, `AutoCuller.cs` |
| Glue service (no UI) | `SimLauncher.Traffic/TrafficMonitorService.cs` |
| Map page (Mapbox GL JS, dead-reckoning interpolation) | `SimLauncher.App/Assets/TrafficMap/map.html` |
| Panel UI | `SimLauncher.App/TrafficWindow.xaml(.cs)`, `ViewModels/TrafficViewModel.cs` |
| Tests | `tests/SimLauncher.Traffic.Tests` |

The map runs in a WebView2 with the WebSocket handled on the .NET side; the page only
renders (aircraft glide between updates via dead-reckoning from `groundspeed` +
`heading`) and posts back `removeRequest` messages. Requires the WebView2 Runtime
(preinstalled on Windows 11).

Declutter: ground traffic (`onGround`) is only rendered at zoom ≥ 8 — zoomed out you
see airborne aircraft only; zoom into an airport and the parked/taxiing traffic
appears. The player is always drawn. The cutoff is the `GROUND_HIDE_BELOW_ZOOM`
constant at the top of `map.html`. Conflict detection is unaffected (it never involves
ground traffic anyway) and the status-bar aircraft count always reflects the full feed.
