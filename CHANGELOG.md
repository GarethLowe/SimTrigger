# Changelog

Newest first. One entry per work session; sections only where they add clarity.

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
