# SimTrigger / SimLauncher — project instructions

## Changelog discipline

After every work session that changes code, config, or behavior, append an entry to
`CHANGELOG.md` (repo root, newest first, date-stamped `## YYYY-MM-DD — <title>`).
Write it like the end-of-turn summary: what was fixed/added/changed and **why**,
including root-cause reasoning for bug fixes, behavioral caveats, and anything the
user must do (rebuild, restart, config edits). Doc-only or changelog-only edits
don't need an entry.
