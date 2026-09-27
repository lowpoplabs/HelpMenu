# Changelog

All notable changes to HelpMenu will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.2] - 2026-09-27

Bundled help pages only; the plugin itself is unchanged.

### Changed
- `IslandTaxi.json`: a note that fares follow the rider's Cobalt standing (Island Taxi 1.6.0 with Cobalt Papers Please).
- `PublicWorks.json`: a note that repair contracts and service payments earn Cobalt standing (Public Works 2.9.0 with Cobalt Papers Please).
- `ServerInfo.json`: the welcome page points new players at `/papers` and the Cobalt Papers Please page.

### Notes
- Cobalt Papers Please ships its own page through the `GetHelpInfo` hook, so it needs no data file.

## [1.0.1] - 2026-09-12

First public release on GitHub — no behaviour changes.

### Added
- One-line load message naming the author and tip jar (`HelpMenu v1.0.1 loaded - by LowPopLabs - ko-fi.com/lowpoplabs`).
- README Support section with the Ko-fi button; support posture is as-is, issues welcome.

## [1.0.0] - 2026-08-31

### Added
- 0.1.0 skeleton: config (`ChatCommands`, `UsePermission`, `HiddenPlugins`), Lang chrome strings, help-entry model, `/help` + `help.open` opening a placeholder CUI panel, `help.ui.close`, admin `help.reload` (catalog stub).
- Catalog ingestion: data-file loader (`oxide/data/HelpMenu/*.json`, per-file validation), `GetHelpInfo` hook sweep (hook shadows data file), loaded-plugin + `HiddenPlugins` filters, dirty-flag lazy rebuild on plugin load/unload, per-viewer permission/auth-level visibility resolver. HelpMenu documents itself through its own hook; placeholder panel now lists visible entries.
- Full CUI: sidebar of plugin buttons (paged past 12) + content pane with description, zebra-striped command table (paged past 8), how-to steps and notes, all hand-wrapped; `<`→`‹` injection guard on ingested text; `/help <plugin>` chat fallback with prefix matching.
- Help content: 12 data files covering the nine LowPopLabs plugins plus XPerience (pointer entry), NightLantern and NoFuelRequirements, all command tables verified against plugin source. Visibility rule refined: entries with only-admin commands stay visible to players when they carry how-to content.

- Playtest feedback round: `Standalone` entry flag + a Welcome page (`ServerInfo.json`) as the first tab; periodic `/help` chat reminder (`ReminderMinutes`) and post-connect hint (`ConnectHint`); Apartment Complex amenities (roof commons, parking lifts, horse troughs), taxi fleet/coverage note, and belt-drinking how-to for Public Works.
- Feedback button in the panel header: an input overlay writes player notes to `oxide/data/HelpMenu/feedback.log` (240-char cap, 30 s cooldown). The Welcome page points at it.
- Taxi entry now lists the real dedicated stops (heli pads at Outpost, Bandit Camp, Radtown, Apartment Complex roof, both Oil Rigs; boat docks at both rigs), read from the live pad config.
- Admin commands removed from all help entries — the menu documents player commands only.
- Content pane is now a vertical scroll view: long entries scroll at natural height instead of truncating (fixes Island Taxi's cut-off notes and Apartment Complex's missing communal note); each note renders as its own bullet.

### Removed
- FakeFriends and PWSatReset help entries (immersion / admin-only); both also added to the live `HiddenPlugins` config so a future hook can't resurface them.

### Fixed
- Guarded against a null `ChatCommands` in a hand-edited config failing the plugin load; removed dead `ColRow`/`UiNotes` remnants (pre-1.0.0 review pass, see docs/reports/2026-08-31-review.md).
