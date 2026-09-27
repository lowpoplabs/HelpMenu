# HelpMenu

*One menu, every plugin, no wiki required.*

An Oxide plugin for Rust that gives players a single `/help` command opening a CUI browser of every player-facing plugin on the server — description, commands, and how-to steps — with a Welcome page, a feedback box, and gentle chat reminders that it exists.

<!-- lpl:links -->
**[Download v1.0.2](https://github.com/lowpoplabs/HelpMenu/releases/latest)** · **Flyer:** [web](https://lowpoplabs.github.io/flyers/HelpMenu.html) / [PDF](HelpMenu-Flyer.pdf) · **[Changelog](CHANGELOG.md)** · **[Ko-fi](https://ko-fi.com/lowpoplabs)**
<!-- /lpl:links -->

## Features

- **`/help` CUI browser** — left sidebar of topics (paged), right pane with description, command table, numbered how-to steps and notes. The content pane scrolls, so long entries are never cut off.
- **Two content sources** — any loaded plugin can implement a `GetHelpInfo()` hook returning a help entry (authoritative, can never drift), and `oxide/data/HelpMenu/<PluginName>.json` data files cover plugins that don't (including third-party ones). Entries for plugins that aren't loaded are ignored, so the menu never advertises a dead feature.
- **Standalone pages** — a data file with `"Standalone": true` needs no backing plugin; use it for a server-info/welcome page.
- **Per-viewer filtering** — commands tagged with an oxide permission or auth level are hidden from players who lack them.
- **Feedback box** — a header button opens an input overlay; notes are appended to `oxide/data/HelpMenu/feedback.log` with a timestamp and the player's name (240-char cap, 30 s cooldown).
- **Chat reminders** — a configurable periodic broadcast and a one-time post-connect hint, both pointing players at `/help`.
- **Chat fallback** — `/help <topic>` prints the same entry as chat text.

## Commands

- `/help` — open the CUI help browser (command configurable)
- `/help <topic>` — print one topic's help as chat text (name or title prefix match)
- `help.open` — console/bindable equivalent of `/help`
- `help.reload` — admin (authLevel 2 or `helpmenu.admin`): re-scan data files without a plugin reload

## Config

- `ChatCommands` — chat command aliases (default `["help"]`)
- `UsePermission` — permission required to use the menu (empty = everyone)
- `HiddenPlugins` — plugin names to never list, even if they provide help
- `ReminderMinutes` — broadcast a `/help` hint in chat this often (default 30; 0 = never)
- `ConnectHint` — whisper the hint to each player shortly after connect (default true)

## Help-entry schema

Same shape for the hook return and data files:

```json
{
  "Plugin": "IslandTaxi",
  "Standalone": false,
  "Title": "Island Taxi",
  "Description": "Call a taxi to travel around the island.",
  "Commands": [
    { "Command": "/taxi", "Args": "", "Description": "Open the taxi menu", "Permission": "", "AuthLevel": 0 }
  ],
  "HowTo": [ "Step one.", "Step two." ],
  "Notes": [ "Optional extra lines." ],
  "Order": 10
}
```

`Plugin` must equal the Oxide plugin **Name** (the class name) — it's the key for the loaded check. A hook result replaces the plugin's data file. An entry is hidden from a viewer only when it has commands, none are visible to them, and it has no how-to content.

## Install

Copy `HelpMenu.cs` to `oxide/plugins/` and the contents of the bundled `data/HelpMenu/` folder to `oxide/data/HelpMenu/`. The bundled files cover a dozen plugins (including a `ServerInfo.json` welcome-page template) — edit or delete to match your server; entries for plugins you don't run are ignored automatically.

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

MIT — see [LICENSE](LICENSE).
