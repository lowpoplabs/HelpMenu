using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;  // ArgEx (arg.Player()) lives in the UnityEngine namespace

namespace Oxide.Plugins
{
    [Info("HelpMenu", "LowPopLabs", "1.0.3")]
    [Description("A /help CUI browser for every player-facing plugin on the server: descriptions, commands and how-to steps, fed by a GetHelpInfo hook or data files and filtered to what each player can actually use.")]
    public class HelpMenu : RustPlugin
    {
        #region Config

        private ConfigData _config;

        private class ConfigData
        {
            [JsonProperty("ChatCommands (chat commands that open the help menu)")]
            public string[] ChatCommands = { "help" };

            [JsonProperty("UsePermission (oxide permission required to use the menu; empty = everyone)")]
            public string UsePermission = "";

            [JsonProperty("HiddenPlugins (plugin names never listed, even if they provide help)")]
            public string[] HiddenPlugins = Array.Empty<string>();

            [JsonProperty("ReminderMinutes (broadcast a /help hint in chat this often; 0 = never)")]
            public float ReminderMinutes = 30f;

            [JsonProperty("ConnectHint (whisper the /help hint to each player shortly after they connect)")]
            public bool ConnectHint = true;
        }

        protected override void LoadDefaultConfig() => _config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try { _config = Config.ReadObject<ConfigData>() ?? new ConfigData(); }
            catch (Exception e) { PrintWarning($"Config unreadable ({e.Message}) — using defaults."); _config = new ConfigData(); }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have access to the help menu.",
                ["UiTitle"] = "SERVER HELP",
                ["UiEmpty"] = "No help entries yet.\n\nPlugins register themselves here — check back soon.",
                ["UiCommands"] = "COMMANDS",
                ["UiHowTo"] = "HOW TO",
                ["UiPage"] = "{0}/{1}",
                ["ChatHowTo"] = "How to:",
                ["ChatUnknown"] = "No help for \"{0}\". Topics: {1}",
                ["ReloadDone"] = "Help catalog rebuilt: {0} entries.",
                ["Reminder"] = "New here, or forgot a command? Type <color=#8bc34a>/{0}</color> for the server guide.",
                ["UiFeedback"] = "Feedback",
                ["UiFeedbackTitle"] = "Send a note to the owner",
                ["UiFeedbackHint"] = "Type your note and press Enter to send.",
                ["UiCancel"] = "Cancel",
                ["FeedbackThanks"] = "Thanks — your note is in the owner's box.",
                ["FeedbackCooldown"] = "Give it a moment before sending more feedback.",
            }, this);
        }

        private string Msg(BasePlayer player, string key, params object[] args)
        {
            var text = lang.GetMessage(key, this, player?.UserIDString);
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private void Tell(BasePlayer player, string key, params object[] args) => SendReply(player, Msg(player, key, args));

        #endregion

        #region Help entry model

        // One schema for both sources: a plugin's GetHelpInfo() return and a
        // data file under oxide/data/HelpMenu/<PluginName>.json deserialize to
        // exactly this shape. Plugin must equal the Oxide plugin Name — it is
        // the key for the loaded check and the hook-shadows-data-file rule.
        private class HelpEntry
        {
            [JsonProperty("Plugin")] public string Plugin = "";
            [JsonProperty("Standalone")] public bool Standalone = false;  // true: no loaded-plugin check (server-info pages)
            [JsonProperty("Title")] public string Title = "";
            [JsonProperty("Description")] public string Description = "";
            [JsonProperty("Commands")] public List<HelpCommand> Commands = new List<HelpCommand>();
            [JsonProperty("HowTo")] public List<string> HowTo = new List<string>();
            [JsonProperty("Notes")] public List<string> Notes = new List<string>();
            [JsonProperty("Order")] public int Order = 100;
        }

        private class HelpCommand
        {
            [JsonProperty("Command")] public string Command = "";
            [JsonProperty("Args")] public string Args = "";
            [JsonProperty("Description")] public string Description = "";
            [JsonProperty("Permission")] public string Permission = "";
            [JsonProperty("AuthLevel")] public int AuthLevel = 0;
        }

        #endregion

        #region Catalog

        private const string DataRoot = "HelpMenu";

        // Keyed by plugin name. Rebuilt lazily: plugin load/unload storms
        // (server boot, batch reloads) just set the dirty flag, and the next
        // /help or help.reload pays for one rebuild.
        private readonly Dictionary<string, HelpEntry> _catalog = new Dictionary<string, HelpEntry>(StringComparer.OrdinalIgnoreCase);
        private bool _catalogDirty = true;
        private readonly HashSet<ulong> _uiOpen = new HashSet<ulong>();

        private string DataDir => System.IO.Path.Combine(Interface.Oxide.DataDirectory, DataRoot);

        private void EnsureCatalog()
        {
            if (_catalogDirty) RebuildCatalog();
        }

        private void RebuildCatalog()
        {
            _catalog.Clear();
            _catalogDirty = false;
            int fromFiles = 0, fromHooks = 0;

            // Data files first — the hook sweep below replaces any entry whose
            // plugin turns out to implement GetHelpInfo, so each plugin has
            // exactly one source of truth (docs/decisions/0001).
            try
            {
                if (System.IO.Directory.Exists(DataDir))
                    foreach (var path in System.IO.Directory.GetFiles(DataDir, "*.json"))
                    {
                        var file = System.IO.Path.GetFileName(path);
                        try
                        {
                            var entry = JsonConvert.DeserializeObject<HelpEntry>(System.IO.File.ReadAllText(path));
                            if (entry == null) { PrintWarning($"Help file {file} is empty — skipped."); continue; }
                            if (string.IsNullOrEmpty(entry.Plugin))
                                entry.Plugin = System.IO.Path.GetFileNameWithoutExtension(path);
                            if (!entry.Standalone && plugins.Find(entry.Plugin) == null) continue;  // never advertise a dead feature
                            Sanitize(entry);
                            _catalog[entry.Plugin] = entry;
                            fromFiles++;
                        }
                        catch (Exception e) { PrintWarning($"Help file {file} unreadable ({e.Message}) — skipped."); }
                    }
            }
            catch (Exception e) { PrintWarning($"Help data dir unreadable ({e.Message}) — data-file entries skipped."); }

            foreach (var plugin in plugins.GetAll())
            {
                object raw;
                try { raw = plugin.Call("GetHelpInfo"); }
                catch (Exception e) { PrintWarning($"{plugin.Name}: GetHelpInfo threw ({e.Message}) — ignored."); continue; }
                if (raw == null) continue;
                try
                {
                    // Round-trip through JSON so hooks may return anything
                    // schema-shaped: Dictionary, JObject, or their own POCO.
                    var entry = JsonConvert.DeserializeObject<HelpEntry>(JsonConvert.SerializeObject(raw));
                    if (entry == null) continue;
                    entry.Plugin = plugin.Name;  // hook entries key by the real plugin, whatever the payload says
                    Sanitize(entry);
                    if (_catalog.ContainsKey(plugin.Name))
                    {
                        fromFiles--;
                        Puts($"{plugin.Name}: GetHelpInfo hook shadows its data file — the file can be deleted.");
                    }
                    _catalog[plugin.Name] = entry;
                    fromHooks++;
                }
                catch (Exception e) { PrintWarning($"{plugin.Name}: GetHelpInfo returned an unusable payload ({e.Message}) — ignored."); }
            }

            if (_config.HiddenPlugins != null)
                foreach (var hidden in _config.HiddenPlugins)
                    if (!string.IsNullOrEmpty(hidden)) _catalog.Remove(hidden);

            Puts($"Help catalog: {_catalog.Count} entries ({fromHooks} via hook, {fromFiles} via data file).");
        }

        private static void Sanitize(HelpEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Title)) entry.Title = entry.Plugin;
            entry.Description = entry.Description ?? "";
            entry.Commands = entry.Commands ?? new List<HelpCommand>();
            entry.Commands.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Command));
            foreach (var command in entry.Commands)
            {
                command.Args = command.Args ?? "";
                command.Description = command.Description ?? "";
                command.Permission = command.Permission ?? "";
            }
            entry.HowTo = entry.HowTo ?? new List<string>();
            entry.HowTo.RemoveAll(string.IsNullOrEmpty);
            entry.Notes = entry.Notes ?? new List<string>();
            entry.Notes.RemoveAll(string.IsNullOrEmpty);
        }

        // A command is visible when the viewer holds its permission (if any)
        // AND meets its auth level (if any).
        private bool CommandVisible(BasePlayer player, HelpCommand command) =>
            (string.IsNullOrEmpty(command.Permission) || permission.UserHasPermission(player.UserIDString, command.Permission)) &&
            (command.AuthLevel <= 0 || (player.Connection != null && player.Connection.authLevel >= command.AuthLevel));

        private List<HelpCommand> VisibleCommands(BasePlayer player, HelpEntry entry)
        {
            var visible = new List<HelpCommand>();
            foreach (var command in entry.Commands)
                if (CommandVisible(player, command)) visible.Add(command);
            return visible;
        }

        // An entry is hidden only when it has commands, none of them visible
        // to this viewer, AND no how-to content. That keeps passive player
        // plugins with an admin command (FakeFriends, LootableObjects) listed
        // for everyone, while pure admin utilities with no how-to
        // (PWSatReset) vanish for regular players.
        private List<HelpEntry> VisibleEntries(BasePlayer player)
        {
            EnsureCatalog();
            var list = new List<HelpEntry>();
            foreach (var entry in _catalog.Values)
                if (entry.Commands.Count == 0 || entry.HowTo.Count > 0 || VisibleCommands(player, entry).Count > 0) list.Add(entry);
            list.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order)
                : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // HelpMenu documents itself through the same hook it sweeps.
        [HookMethod("GetHelpInfo")]
        private object GetHelpInfo() => new HelpEntry
        {
            Plugin = "HelpMenu",
            Title = "Server Help",
            Description = "This menu. Every plugin on the server documents itself here.",
            Commands = new List<HelpCommand>
            {
                new HelpCommand { Command = "/help", Description = "Open the help menu" },
                new HelpCommand { Command = "/help", Args = "<plugin>", Description = "Print one plugin's help in chat" },
            },
            Order = 95,  // meta-help belongs at the bottom; the ServerInfo page owns the first tab
        };

        #endregion

        #region Lifecycle

        // Admin commands accept either this permission or auth level 2.
        private const string AdminPermission = "helpmenu.admin";

        private void Init()
        {
            if (!string.IsNullOrEmpty(_config.UsePermission))
                permission.RegisterPermission(_config.UsePermission, this);
            permission.RegisterPermission(AdminPermission, this);
            foreach (var command in _config.ChatCommands ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(command)) cmd.AddChatCommand(command, this, nameof(ChatOpen));
            Puts($"HelpMenu v{Version} loaded - by LowPopLabs - ko-fi.com/lowpoplabs");
        }

        private void OnServerInitialized()
        {
            RebuildCatalog();
            if (_config.ReminderMinutes > 0)
                timer.Every(_config.ReminderMinutes * 60f, BroadcastReminder);
        }

        private void BroadcastReminder()
        {
            foreach (var player in BasePlayer.activePlayerList)
                Tell(player, "Reminder", FirstChatCommand());
        }

        private string FirstChatCommand()
        {
            if (_config.ChatCommands != null)
                foreach (var command in _config.ChatCommands)
                    if (!string.IsNullOrEmpty(command)) return command;
            return "help";
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!_config.ConnectHint) return;
            var userId = player.userID;
            // Let the loading screen finish before whispering the hint.
            timer.Once(45f, () =>
            {
                var online = BasePlayer.FindByID(userId);
                if (online != null && online.IsConnected) Tell(online, "Reminder", FirstChatCommand());
            });
        }

        // Load/unload storms during boot or batch reloads only dirty the
        // flag; EnsureCatalog pays for one rebuild on the next open.
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != this) _catalogDirty = true;
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin != this) _catalogDirty = true;
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, UiRoot);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            _uiOpen.Remove(player.userID);
            _selected.Remove(player.userID);
            _sidebarPage.Remove(player.userID);
            _commandPage.Remove(player.userID);
            _feedbackOpen.Remove(player.userID);
            _lastFeedback.Remove(player.userID);
        }

        private bool Allowed(BasePlayer player) =>
            string.IsNullOrEmpty(_config.UsePermission) ||
            permission.UserHasPermission(player.UserIDString, _config.UsePermission);

        #endregion

        #region Commands

        private void ChatOpen(BasePlayer player, string command, string[] args)
        {
            if (!Allowed(player)) { Tell(player, "NoPermission"); return; }
            if (args != null && args.Length > 0) { ChatHelp(player, string.Join(" ", args)); return; }
            OpenUi(player);
        }

        // /help <plugin> — the same entry, as chat text.
        private void ChatHelp(BasePlayer player, string query)
        {
            var entries = VisibleEntries(player);
            HelpEntry match = null;
            foreach (var entry in entries)
                if (entry.Plugin.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                    entry.Title.Equals(query, StringComparison.OrdinalIgnoreCase)) { match = entry; break; }
            if (match == null)
                foreach (var entry in entries)
                    if (entry.Plugin.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                        entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) { match = entry; break; }

            if (match == null)
            {
                var topics = entries.ConvertAll(e => e.Title);
                Tell(player, "ChatUnknown", Ink(query), string.Join(", ", topics));
                return;
            }

            var sb = new StringBuilder();
            sb.Append("<color=#d8c48a>").Append(Ink(match.Title)).Append("</color>");
            if (!string.IsNullOrEmpty(match.Description)) sb.Append(" — ").Append(Ink(match.Description));
            foreach (var cmd in VisibleCommands(player, match))
            {
                sb.Append("\n<color=#a8c8a0>").Append(Ink(cmd.Command));
                if (!string.IsNullOrEmpty(cmd.Args)) sb.Append(' ').Append(Ink(cmd.Args));
                sb.Append("</color>");
                if (!string.IsNullOrEmpty(cmd.Description)) sb.Append(" — ").Append(Ink(cmd.Description));
            }
            if (match.HowTo.Count > 0)
            {
                sb.Append('\n').Append(Msg(player, "ChatHowTo"));
                for (var i = 0; i < match.HowTo.Count; i++)
                    sb.Append("\n  ").Append(i + 1).Append(". ").Append(Ink(match.HowTo[i]));
            }
            SendReply(player, sb.ToString());
        }

        [ConsoleCommand("help.open")]
        private void CmdOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!Allowed(player)) { Tell(player, "NoPermission"); return; }
            OpenUi(player);
        }

        [ConsoleCommand("help.ui.close")]
        private void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRoot);
            _uiOpen.Remove(player.userID);
        }

        [ConsoleCommand("help.ui.select")]
        private void CmdSelect(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID) || !Allowed(player)) return;
            _selected[player.userID] = arg.GetString(0, "");
            _commandPage[player.userID] = 0;
            OpenUi(player);
        }

        [ConsoleCommand("help.ui.side")]
        private void CmdSidebarPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID) || !Allowed(player)) return;
            Step(_sidebarPage, player.userID, arg.GetString(0, ""));
            OpenUi(player);
        }

        [ConsoleCommand("help.ui.cmdpage")]
        private void CmdCommandPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID) || !Allowed(player)) return;
            Step(_commandPage, player.userID, arg.GetString(0, ""));
            OpenUi(player);
        }

        // Moves a page index by one; the render pass clamps to the real range.
        private static void Step(Dictionary<ulong, int> index, ulong userId, string direction)
        {
            int page;
            index.TryGetValue(userId, out page);
            index[userId] = Math.Max(0, page + (direction == "next" ? 1 : -1));
        }

        [ConsoleCommand("help.ui.feedback")]
        private void CmdFeedbackOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID) || !Allowed(player)) return;
            _feedbackOpen.Add(player.userID);
            OpenUi(player);
        }

        [ConsoleCommand("help.ui.feedbackcancel")]
        private void CmdFeedbackCancel(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            _feedbackOpen.Remove(player.userID);
            if (_uiOpen.Contains(player.userID)) OpenUi(player);
        }

        // Fired by the input field on Enter; everything after the command
        // name is the note.
        [ConsoleCommand("help.ui.sendfeedback")]
        private void CmdSendFeedback(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_uiOpen.Contains(player.userID) || !Allowed(player)) return;

            var text = OneLine(arg.FullString.ToString());
            if (text.Length > FeedbackMaxChars) text = text.Substring(0, FeedbackMaxChars);
            _feedbackOpen.Remove(player.userID);
            OpenUi(player);
            if (text.Length == 0) return;  // Enter on an empty box = cancel

            float last;
            if (_lastFeedback.TryGetValue(player.userID, out last) && Time.realtimeSinceStartup - last < FeedbackCooldownSeconds)
            {
                Tell(player, "FeedbackCooldown");
                return;
            }
            _lastFeedback[player.userID] = Time.realtimeSinceStartup;

            try
            {
                var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] {OneLine(player.displayName)} ({player.UserIDString}): {text}{Environment.NewLine}";
                System.IO.File.AppendAllText(System.IO.Path.Combine(DataDir, "feedback.log"), line);
                Tell(player, "FeedbackThanks");
            }
            catch (Exception e) { PrintWarning($"Could not write feedback.log: {e.Message}"); }
        }

        private static string OneLine(string text) =>
            (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();

        private bool IsAdmin(ConsoleSystem.Arg arg) =>
            arg.Connection == null ||  // server console
            arg.Connection.authLevel >= 2 ||
            permission.UserHasPermission(arg.Connection.userid.ToString(), AdminPermission);

        [ConsoleCommand("help.reload")]
        private void CmdReload(ConsoleSystem.Arg arg)
        {
            if (!IsAdmin(arg)) return;
            RebuildCatalog();
            arg.ReplyWith(string.Format(lang.GetMessage("ReloadDone", this), _catalog.Count));
        }

        #endregion

        #region UI

        private const string UiRoot = "help.root";
        private const int SidebarRows = 12;
        private const int CommandsPerPage = 8;

        // Palette shared with the Scrapbook book so the server UIs feel like
        // one family.
        private const string ColPanel = "0.13 0.10 0.08 0.98";
        private const string ColInset = "0.10 0.08 0.06 1";
        private const string ColBody = "0.16 0.13 0.10 1";
        private const string ColCream = "0.87 0.83 0.74 1";
        private const string ColText = "0.80 0.76 0.68 1";
        private const string ColMuted = "0.55 0.51 0.44 1";
        private const string ColGold = "0.82 0.69 0.38 1";
        private const string ColButton = "0.25 0.22 0.18 1";
        private const string ColButtonLit = "0.33 0.29 0.23 1";

        private const int FeedbackMaxChars = 240;
        private const float FeedbackCooldownSeconds = 30f;

        private readonly Dictionary<ulong, string> _selected = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, int> _sidebarPage = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _commandPage = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> _feedbackOpen = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _lastFeedback = new Dictionary<ulong, float>();

        // 900x590 fixed-pixel panel on the 1280x720 CUI canvas: header bar,
        // sidebar of plugin buttons (paged past 12), content pane with
        // description, command table (paged past 8) and how-to steps. All
        // text budgets are hand-wrapped — Unity clips silently.
        private void OpenUi(BasePlayer player)
        {
            var entries = VisibleEntries(player);
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.45", Material = "assets/content/ui/uibackgroundblur.mat" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", UiRoot);

            ui.Add(new CuiPanel
            {
                Image = { Color = ColPanel },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-450 -295", OffsetMax = "450 295" },
            }, UiRoot, "help.panel");

            ui.Add(new CuiLabel
            {
                Text = { Text = Msg(player, "UiTitle"), FontSize = 20, Align = TextAnchor.MiddleLeft, Color = ColCream },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "24 550", OffsetMax = "400 586" },
            }, "help.panel");

            ui.Add(new CuiButton
            {
                Button = { Color = "0.55 0.20 0.16 1", Command = "help.ui.close" },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.9 0.85 0.8 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "848 550", OffsetMax = "884 586" },
            }, "help.panel");

            ui.Add(new CuiButton
            {
                Button = { Color = ColButton, Command = "help.ui.feedback" },
                Text = { Text = Msg(player, "UiFeedback"), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColCream },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "736 552", OffsetMax = "840 584" },
            }, "help.panel");

            if (entries.Count == 0)
            {
                ui.Add(new CuiLabel
                {
                    Text = { Text = Msg(player, "UiEmpty"), FontSize = 15, Align = TextAnchor.MiddleCenter, Color = ColMuted },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "24 24", OffsetMax = "-24 -60" },
                }, "help.panel");
            }
            else
            {
                string selectedName;
                _selected.TryGetValue(player.userID, out selectedName);
                HelpEntry current = null;
                foreach (var entry in entries)
                    if (entry.Plugin.Equals(selectedName, StringComparison.OrdinalIgnoreCase)) { current = entry; break; }
                if (current == null) current = entries[0];
                _selected[player.userID] = current.Plugin;

                BuildSidebar(ui, player, entries, current);
                BuildContent(ui, player, current);
            }

            if (_feedbackOpen.Contains(player.userID)) BuildFeedback(ui, player);

            CuiHelper.DestroyUi(player, UiRoot);
            CuiHelper.AddUi(player, ui);
            _uiOpen.Add(player.userID);
        }

        private void BuildFeedback(CuiElementContainer ui, BasePlayer player)
        {
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
            }, "help.panel", "help.fb");

            ui.Add(new CuiPanel
            {
                Image = { Color = ColBody },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-300 -95", OffsetMax = "300 95" },
            }, "help.fb", "help.fbbox");

            ui.Add(new CuiLabel
            {
                Text = { Text = Msg(player, "UiFeedbackTitle"), FontSize = 15, Align = TextAnchor.MiddleLeft, Color = ColCream },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "20 144", OffsetMax = "580 174" },
            }, "help.fbbox");

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.87 0.83 0.74 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "20 84", OffsetMax = "580 134" },
            }, "help.fbbox", "help.fbfield");

            ui.Add(new CuiElement
            {
                Parent = "help.fbfield",
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        FontSize = 13,
                        CharsLimit = FeedbackMaxChars,
                        Command = "help.ui.sendfeedback",
                        Color = "0.15 0.12 0.10 1",
                        Align = TextAnchor.MiddleLeft,
                        NeedsKeyboard = true,
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 0", OffsetMax = "-10 0" },
                },
            });

            ui.Add(new CuiLabel
            {
                Text = { Text = Msg(player, "UiFeedbackHint"), FontSize = 11, Align = TextAnchor.MiddleLeft, Color = ColMuted },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "20 52", OffsetMax = "580 76" },
            }, "help.fbbox");

            ui.Add(new CuiButton
            {
                Button = { Color = ColButton, Command = "help.ui.feedbackcancel" },
                Text = { Text = Msg(player, "UiCancel"), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColCream },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "460 12", OffsetMax = "580 44" },
            }, "help.fbbox");
        }

        private void BuildSidebar(CuiElementContainer ui, BasePlayer player, List<HelpEntry> entries, HelpEntry current)
        {
            ui.Add(new CuiPanel
            {
                Image = { Color = ColInset },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "16 16", OffsetMax = "228 540" },
            }, "help.panel", "help.side");

            var paged = entries.Count > SidebarRows;
            var rows = paged ? SidebarRows - 1 : SidebarRows;  // bottom slot becomes the pager
            var pages = paged ? (entries.Count + rows - 1) / rows : 1;
            int page;
            _sidebarPage.TryGetValue(player.userID, out page);
            page = Math.Min(Math.Max(page, 0), pages - 1);
            _sidebarPage[player.userID] = page;

            var start = page * rows;
            var end = Math.Min(start + rows, entries.Count);
            for (var i = start; i < end; i++)
            {
                var entry = entries[i];
                var isCurrent = ReferenceEquals(entry, current);
                var top = 516 - (i - start) * 40;
                ui.Add(new CuiButton
                {
                    Button = { Color = isCurrent ? ColButtonLit : "0.16 0.13 0.10 1", Command = "help.ui.select " + entry.Plugin },
                    Text = { Text = Ink(Clip(entry.Title, 24)), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = isCurrent ? "0.90 0.86 0.77 1" : ColMuted },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"6 {top - 36}", OffsetMax = $"206 {top}" },
                }, "help.side");
            }

            if (paged)
            {
                ui.Add(new CuiButton
                {
                    Button = { Color = ColButton, Command = "help.ui.side prev" },
                    Text = { Text = "‹", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = ColCream },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "6 6", OffsetMax = "62 38" },
                }, "help.side");
                ui.Add(new CuiLabel
                {
                    Text = { Text = Msg(player, "UiPage", page + 1, pages), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColMuted },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "62 6", OffsetMax = "150 38" },
                }, "help.side");
                ui.Add(new CuiButton
                {
                    Button = { Color = ColButton, Command = "help.ui.side next" },
                    Text = { Text = "›", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = ColCream },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "150 6", OffsetMax = "206 38" },
                }, "help.side");
            }
        }

        // The pane is a vertical scroll view: content is laid out top-down at
        // its natural height and never truncated — long entries scroll
        // instead of losing their notes. The scroll element is inserted after
        // layout, once the total content height is known.
        private void BuildContent(CuiElementContainer ui, BasePlayer player, HelpEntry entry)
        {
            ui.Add(new CuiPanel
            {
                Image = { Color = ColBody },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "240 16", OffsetMax = "884 540" },
            }, "help.panel", "help.body");

            var scrollAt = ui.Count;  // the scroll view is inserted here once the height is known

            // Cursor grows downward from the content's top edge; children
            // anchor to the top so a taller-than-pane content rect scrolls.
            var y = 12;

            AddBlock(ui, Ink(entry.Title), 18, null, ColCream, y, 28);
            y += 32;

            var descLines = Wrap(Ink(entry.Description), 70);
            if (descLines.Count > 0)
            {
                var height = descLines.Count * 17;
                AddBlock(ui, string.Join("\n", descLines), 13, "robotocondensed-regular.ttf", ColText, y, height);
                y += height + 12;
            }

            var visible = VisibleCommands(player, entry);
            if (visible.Count > 0)
            {
                var pages = (visible.Count + CommandsPerPage - 1) / CommandsPerPage;
                int page;
                _commandPage.TryGetValue(player.userID, out page);
                page = Math.Min(Math.Max(page, 0), pages - 1);
                _commandPage[player.userID] = page;

                AddBlock(ui, Msg(player, "UiCommands"), 12, null, ColGold, y, 20);
                if (pages > 1)
                {
                    ui.Add(new CuiButton
                    {
                        Button = { Color = ColButton, Command = "help.ui.cmdpage prev" },
                        Text = { Text = "‹", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColCream },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"492 {-(y + 20)}", OffsetMax = $"522 {-y}" },
                    }, "help.scroll");
                    ui.Add(new CuiLabel
                    {
                        Text = { Text = Msg(player, "UiPage", page + 1, pages), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = ColMuted },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"522 {-(y + 20)}", OffsetMax = $"590 {-y}" },
                    }, "help.scroll");
                    ui.Add(new CuiButton
                    {
                        Button = { Color = ColButton, Command = "help.ui.cmdpage next" },
                        Text = { Text = "›", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColCream },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"590 {-(y + 20)}", OffsetMax = $"620 {-y}" },
                    }, "help.scroll");
                }
                y += 24;

                var first = page * CommandsPerPage;
                var last = Math.Min(first + CommandsPerPage, visible.Count);
                for (var i = first; i < last; i++)
                {
                    var command = visible[i];
                    if ((i - first) % 2 == 0)
                        ui.Add(new CuiPanel
                        {
                            Image = { Color = "1 1 1 0.03" },
                            RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"12 {-(y + 24)}", OffsetMax = $"624 {-y}" },
                        }, "help.scroll");
                    var invocation = string.IsNullOrEmpty(command.Args) ? command.Command : command.Command + " " + command.Args;
                    ui.Add(new CuiLabel
                    {
                        Text = { Text = Ink(Clip(invocation, 30)), FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColGold },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"16 {-(y + 24)}", OffsetMax = $"250 {-y}" },
                    }, "help.scroll");
                    ui.Add(new CuiLabel
                    {
                        Text = { Text = Ink(Clip(command.Description, 50)), FontSize = 12, Font = "robotocondensed-regular.ttf", Align = TextAnchor.MiddleLeft, Color = ColText },
                        RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"258 {-(y + 24)}", OffsetMax = $"620 {-y}" },
                    }, "help.scroll");
                    y += 26;
                }
                y += 10;
            }

            if (entry.HowTo.Count > 0)
            {
                AddBlock(ui, Msg(player, "UiHowTo"), 12, null, ColGold, y, 20);
                y += 24;

                for (var i = 0; i < entry.HowTo.Count; i++)
                {
                    var lines = Wrap((i + 1) + ".  " + Ink(entry.HowTo[i]), 70);
                    var height = lines.Count * 16;
                    AddBlock(ui, string.Join("\n", lines), 13, "robotocondensed-regular.ttf", ColText, y, height);
                    y += height + 6;
                }
                y += 6;
            }

            foreach (var note in entry.Notes)
            {
                var lines = Wrap("·  " + Ink(note), 76);
                var height = lines.Count * 15;
                AddBlock(ui, string.Join("\n", lines), 11, "robotocondensed-regular.ttf", ColMuted, y, height);
                y += height + 4;
            }

            var contentHeight = Math.Max(y + 12, 524);
            ui.Insert(scrollAt, new CuiElement
            {
                Name = "help.scroll",
                Parent = "help.body",
                Components =
                {
                    new CuiScrollViewComponent
                    {
                        Vertical = true,
                        Horizontal = false,
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = $"0 -{contentHeight}", OffsetMax = "0 0" },
                        VerticalScrollbar = new CuiScrollbar { Size = 6f, AutoHide = true, HandleColor = "0.40 0.36 0.30 1", TrackColor = "0 0 0 0.25" },
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                },
            });
        }

        // One top-anchored text block inside the scroll content, y measured
        // downward from the content's top edge.
        private static void AddBlock(CuiElementContainer ui, string text, int fontSize, string font, string color, int y, int height)
        {
            var label = new CuiLabel
            {
                Text = { Text = text, FontSize = fontSize, Align = height > 24 ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft, Color = color },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"16 {-(y + height)}", OffsetMax = $"620 {-y}" },
            };
            if (font != null) label.Text.Font = font;
            ui.Add(label, "help.scroll");
        }

        // Display-only guard: help text is ingested data, and CUI labels parse
        // rich-text tags. Swapping the bracket kills the markup without
        // touching the stored text (same trick as Scrapbook).
        private static string Ink(string text) => text == null ? "" : text.Replace('<', '‹');

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        // Greedy word wrap against a conservative character budget — CUI has
        // no text measurement, so budgets are calibrated per font size the
        // same way Scrapbook's paper pages are.
        private static List<string> Wrap(string text, int max)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            foreach (var hard in text.Split('\n'))
            {
                var line = "";
                foreach (var word in hard.Split(' '))
                {
                    if (line.Length == 0) line = word;
                    else if (line.Length + 1 + word.Length <= max) line += " " + word;
                    else { lines.Add(line); line = word; }
                }
                lines.Add(line);
            }
            return lines;
        }

        #endregion
    }
}
