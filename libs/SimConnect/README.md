# Vendored SimConnect DLLs (local only — NOT in the repo)

**The two DLLs this directory is for are deliberately gitignored.** They are
Microsoft SDK components and not redistributable, so only this README is
committed. A fresh clone has an empty `libs/SimConnect/` and must supply them.

Populate it by copying from an installed MSFS SDK's `SimConnect SDK\lib\`:

- `SimConnect.dll` → `libs/SimConnect/SimConnect.dll`
- `managed\Microsoft.FlightSimulator.SimConnect.dll` → `libs/SimConnect/`

(Last copied 2026-07-18 from `C:\MSFS 2024 SDK\`; the DLLs carry no embedded
version info, hence the date. Re-copy after an SDK update.)

This directory is only a fallback for machines with no MSFS SDK installed. An
installed SDK (`-p:SimConnectSdkDir`, `%MSFS2024_SDK%`, `%MSFS_SDK%`) always
takes precedence — see `src/SimConnectSdk.props`. If neither is present the
build still succeeds, but compiles the SimConnect **stub**: it emits a warning
and sim state detection is disabled at runtime.
