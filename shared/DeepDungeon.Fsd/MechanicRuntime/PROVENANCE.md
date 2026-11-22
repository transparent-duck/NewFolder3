# Provenance

Vendored from `Refs/MasterPlayer/MechanicRuntime` (MasterPlayer commit
`47702cf4975806019483cd2ecd945223f0536649`), which is itself a stripped fork of
BossModReborn pinned to upstream `f6914f3`. License: BSD (`LICENSE.BossModReborn`).

- `Upstream/` — BossModReborn code (modules, WorldState, BossModule, AIHints,
  obstacle maps). Local additions carry known single-hit lifetimes and stack, knockback
  and chase inputs with AIHints. Unmarked forbidden zones retain their original
  shape and activation. Planning and movement remain in `Hosting/`.
  The2026-11-17 host extension adds action/timestamp provenance to knockback
  forecasts and marks launch constraints within their originating component's
  hint range. Native timing data and motion simulation remain in `Hosting/`;
  no per-boss timing constants were added.
  The 2026-11-18 readiness adaptation adds `BossModuleManager.ModuleUpdateFailed`
  so hosts retain an update exception before BMR unloads the failed module.
  Module selection and mechanic algorithms are unchanged.
The local 2026-11-18 composition uses the neutral assembly name
`DeepDungeon.MechanicRuntime` and planning namespace `DeepDungeon.MechanicPlanning`.
Upstream module namespaces, source attribution and BSD licensing are retained.

- `Hosting/` — MP's host-adaptation layer (~25 files, `SourceChanges.json`).
- `SourceChanges.json` — MP's manifest of adapted files with SHA-256 before/after.
  Keep it updated when replaying the strip against a newer BMR upstream (D-10).

Exclusions already applied by MP: plugin entry, WorldStateGameSync, AI movement,
pathfinding engine, MovementOverride. See `Refs/MasterPlayer` for the originals.
