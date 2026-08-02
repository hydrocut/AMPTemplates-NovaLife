using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using UnityEngine;

namespace TeamKitIntro
{
    /// <summary>
    /// TeamKitIntro v2.0 — TeamKit.fr
    ///
    /// Écran d'accueil affiché aux joueurs à l'apparition :
    /// bienvenue, règlement, site, Discord et messages de pub rotatifs.
    /// Entièrement configurable via Plugins/TeamKitIntro/config.json,
    /// lui-même géré depuis le panel AMP (section « Écran d'accueil »).
    ///
    /// v2.0 :
    ///  - Pubs rotatives (« Le saviez-vous ? ») : adMessage1/2/3
    ///  - Sous-titre affiché
    ///  - Migration auto du config.json (réécrit avec toutes les clés)
    ///  - Support des \n littéraux saisis depuis AMP
    ///  - Commandes /intro et /introreset conservées
    /// </summary>
    public class TeamKitIntroPlugin : Plugin
    {
        private const string Version = "2.0.0";

        private string pluginDirectoryPath;
        private string configPath;
        private string seenPlayersPath;

        private IntroConfig config;
        private SeenPlayersDatabase seenPlayers;

        private SChatCommand introCommand;
        private SChatCommand introResetCommand;

        private static int adRotationIndex;

        public TeamKitIntroPlugin(IGameAPI api) : base(api)
        {
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            try
            {
                InitFiles();
                RegisterCommands();
                Debug.Log("[TeamKitIntro v" + Version + "] success : initialisé");
            }
            catch (Exception ex)
            {
                Debug.LogError("[TeamKitIntro] erreur initialisation : " + ex);
            }
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            try
            {
                if (player != null && config != null && config.enabled && config.autoOpenOnSpawn)
                {
                    string steamId = GetPlayerSteamId(player);
                    if (!config.showOnlyFirstJoin || !seenPlayers.HasSeenIntro(steamId))
                    {
                        ShowIntroPanel(player, manualOpen: false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TeamKitIntro] erreur OnPlayerSpawnCharacter : " + ex);
            }
        }

        private void RegisterCommands()
        {
            introCommand = new SChatCommand("/intro", "Affiche l'accueil TeamKit.", "/intro", delegate(Player player, string[] args)
            {
                ShowIntroPanel(player, manualOpen: true);
            });
            introCommand.Register();

            if (config.enableResetCommand)
            {
                introResetCommand = new SChatCommand("/introreset", "Réinitialise ton intro TeamKit.", "/introreset", delegate(Player player, string[] args)
                {
                    string steamId = GetPlayerSteamId(player);
                    seenPlayers.Remove(steamId);
                    SaveSeenPlayers();
                    player.SendText("[TeamKitIntro] Ton intro a été réinitialisée. Tape /intro ou reconnecte-toi.");
                });
                introResetCommand.Register();
            }
        }

        private void ShowIntroPanel(Player player, bool manualOpen)
        {
            if (player == null)
            {
                return;
            }
            string text = BuildIntroText(player, manualOpen);
            UIPanel panel = new UIPanel(config.title, UIPanel.PanelType.Text)
                .SetText(text)
                .AddButton(ShortButton(config.enterButtonText, "Entrer"), delegate(UIPanel ui)
                {
                    MarkIntroSeen(player);
                    player.ClosePanel(ui);
                    if (!string.IsNullOrEmpty(config.enterChatMessage))
                    {
                        player.SendText(config.enterChatMessage);
                    }
                })
                .AddButton(ShortButton(config.rulesButtonText, "Regles"), delegate(UIPanel ui)
                {
                    player.ClosePanel(ui);
                    ShowRulesPanel(player);
                });
            player.ShowPanelUI(panel);
        }

        private void ShowRulesPanel(Player player)
        {
            UIPanel panel = new UIPanel(config.rulesTitle, UIPanel.PanelType.Text)
                .SetText(WrapText(Multiline(config.rulesText), 36))
                .AddButton("Retour", delegate(UIPanel ui)
                {
                    player.ClosePanel(ui);
                    ShowIntroPanel(player, manualOpen: true);
                })
                .AddButton(ShortButton(config.closeButtonText, "Fermer"), delegate(UIPanel ui)
                {
                    player.ClosePanel(ui);
                });
            player.ShowPanelUI(panel);
        }

        private string BuildIntroText(Player player, bool manualOpen)
        {
            string name = Shorten(GetPlayerName(player), 18);
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(config.subtitle))
            {
                sb.AppendLine(WrapText(config.subtitle, 30));
                sb.AppendLine();
            }
            sb.AppendLine(WrapText("Bienvenue " + name + " !", 30));
            sb.AppendLine(WrapText(Multiline(config.welcomeText), 30));
            sb.AppendLine();
            if (!string.IsNullOrEmpty(config.website))
            {
                sb.AppendLine("Site : " + Shorten(config.website, 28));
            }
            if (!string.IsNullOrEmpty(config.discord))
            {
                sb.AppendLine("Discord : " + Shorten(config.discord, 28));
            }
            string ad = NextAd();
            if (ad != null)
            {
                sb.AppendLine();
                sb.AppendLine("Le saviez-vous ?");
                sb.AppendLine(WrapText(ad, 30));
            }
            sb.AppendLine();
            sb.AppendLine(manualOpen ? "Ouverture manuelle." : "Clique sur Entrer.");
            return sb.ToString();
        }

        // Renvoie la prochaine pub non vide, en tournant à chaque affichage
        private string NextAd()
        {
            List<string> ads = new List<string>();
            if (!string.IsNullOrEmpty(config.adMessage1)) ads.Add(config.adMessage1);
            if (!string.IsNullOrEmpty(config.adMessage2)) ads.Add(config.adMessage2);
            if (!string.IsNullOrEmpty(config.adMessage3)) ads.Add(config.adMessage3);
            if (ads.Count == 0)
            {
                return null;
            }
            string ad = ads[adRotationIndex % ads.Count];
            adRotationIndex++;
            return ad;
        }

        // Convertit les "\n" littéraux (saisis depuis un champ AMP) en vrais retours à la ligne
        private static string Multiline(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return value.Replace("\\n", "\n");
        }

        private static string ShortButton(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value))
            {
                value = fallback;
            }
            value = value.Trim();
            if (value.Equals("Entrer en ville", StringComparison.OrdinalIgnoreCase))
            {
                return "Entrer";
            }
            if (value.Equals("Règlement", StringComparison.OrdinalIgnoreCase) || value.Equals("Reglement", StringComparison.OrdinalIgnoreCase))
            {
                return "Regles";
            }
            return Shorten(value, 10);
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "Joueur";
            }
            value = value.Trim();
            if (value.Length <= maxLength)
            {
                return value;
            }
            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private static string WrapText(string text, int maxLineLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            text = text.Replace("\r", "");
            string[] lines = text.Split(new char[1] { '\n' });
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    sb.AppendLine();
                    continue;
                }
                string[] words = line.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int len = 0;
                foreach (string word in words)
                {
                    if (len > 0 && len + 1 + word.Length > maxLineLength)
                    {
                        sb.AppendLine();
                        len = 0;
                    }
                    if (len > 0)
                    {
                        sb.Append(' ');
                        len++;
                    }
                    sb.Append(word);
                    len += word.Length;
                }
                if (i < lines.Length - 1)
                {
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        private void MarkIntroSeen(Player player)
        {
            string steamId = GetPlayerSteamId(player);
            string name = GetPlayerName(player);
            seenPlayers.MarkSeen(steamId, name);
            SaveSeenPlayers();
        }

        private void InitFiles()
        {
            pluginDirectoryPath = Path.Combine(pluginsPath, "TeamKitIntro");
            configPath = Path.Combine(pluginDirectoryPath, "config.json");
            seenPlayersPath = Path.Combine(pluginDirectoryPath, "seen_players.json");
            if (!Directory.Exists(pluginDirectoryPath))
            {
                Directory.CreateDirectory(pluginDirectoryPath);
            }
            LoadOrCreateConfig();
            LoadOrCreateSeenPlayers();
        }

        private void LoadOrCreateConfig()
        {
            if (!File.Exists(configPath))
            {
                config = IntroConfig.CreateDefault();
            }
            else
            {
                config = SimpleJsonCompat.FromJsonConfig(File.ReadAllText(configPath));
                if (config == null)
                {
                    config = IntroConfig.CreateDefault();
                    Debug.LogWarning("[TeamKitIntro] config.json invalide, config par défaut recréée.");
                }
            }
            // Migration : réécrit toujours le fichier avec toutes les clés,
            // pour que l'AutoMap AMP puisse écrire les nouveaux champs.
            File.WriteAllText(configPath, SimpleJsonCompat.ToJson(config));
        }

        private void LoadOrCreateSeenPlayers()
        {
            if (!File.Exists(seenPlayersPath))
            {
                seenPlayers = new SeenPlayersDatabase();
                SaveSeenPlayers();
                return;
            }
            seenPlayers = SimpleJsonCompat.FromJsonSeenPlayers(File.ReadAllText(seenPlayersPath));
            if (seenPlayers == null)
            {
                seenPlayers = new SeenPlayersDatabase();
            }
            if (seenPlayers.players == null)
            {
                seenPlayers.players = new List<SeenPlayerRecord>();
            }
        }

        private void SaveSeenPlayers()
        {
            File.WriteAllText(seenPlayersPath, SimpleJsonCompat.ToJson(seenPlayers));
        }

        private string GetPlayerSteamId(Player player)
        {
            try
            {
                return player.steamId.ToString();
            }
            catch
            {
                return "unknown";
            }
        }

        private string GetPlayerName(Player player)
        {
            try
            {
                if (!string.IsNullOrEmpty(player.FullName))
                {
                    return player.FullName;
                }
            }
            catch
            {
            }
            return "Joueur";
        }
    }

    [Serializable]
    public class IntroConfig
    {
        public bool enabled = true;
        public bool autoOpenOnSpawn = true;
        public bool showOnlyFirstJoin;
        public bool enableResetCommand = true;
        public string title = "TeamKit.fr | Nova-Life RP";
        public string subtitle = "Serveur RP français gratuit et communautaire";
        public string welcomeText = "TeamKit te souhaite la bienvenue. Respecte le RP, joue proprement, et construis ton histoire en ville.";
        public string rulesTitle = "Règlement TeamKit";
        public string rulesText = "1. Respect obligatoire entre joueurs.\n2. Pas de troll, freekill ou abus HRP.\n3. Respecte les scènes RP.\n4. Écoute le staff.\n5. Le serveur est gratuit : aide la communauté à grandir.";
        public string website = "https://www.teamkit.fr";
        public string discord = "https://discord.gg/JXAxAupBqz";
        public string enterButtonText = "Entrer";
        public string rulesButtonText = "Regles";
        public string closeButtonText = "Fermer";
        public string enterChatMessage = "Bienvenue sur TeamKit.fr | Nova-Life RP.";
        public string adMessage1 = "Ce serveur est hébergé gratuitement par TeamKit.fr — découvre nos autres serveurs sur le site !";
        public string adMessage2 = "Rejoins la communauté TeamKit sur Discord pour les événements et les annonces.";
        public string adMessage3 = "TeamKit héberge aussi des serveurs DayZ, Eco et Garry's Mod — viens tester !";

        public static IntroConfig CreateDefault()
        {
            return new IntroConfig();
        }
    }

    [Serializable]
    public class SeenPlayerRecord
    {
        public string steamId;
        public string name;
        public bool seenIntro;
        public string firstSeen;
        public string lastSeen;
    }

    [Serializable]
    public class SeenPlayersDatabase
    {
        public List<SeenPlayerRecord> players = new List<SeenPlayerRecord>();

        public bool HasSeenIntro(string steamId)
        {
            foreach (SeenPlayerRecord r in players)
            {
                if (r != null && r.steamId == steamId && r.seenIntro)
                {
                    return true;
                }
            }
            return false;
        }

        public void MarkSeen(string steamId, string name)
        {
            foreach (SeenPlayerRecord r in players)
            {
                if (r != null && r.steamId == steamId)
                {
                    r.seenIntro = true;
                    r.name = name;
                    r.lastSeen = DateTime.UtcNow.ToString("o");
                    return;
                }
            }
            players.Add(new SeenPlayerRecord
            {
                steamId = steamId,
                name = name,
                seenIntro = true,
                firstSeen = DateTime.UtcNow.ToString("o"),
                lastSeen = DateTime.UtcNow.ToString("o")
            });
        }

        public void Remove(string steamId)
        {
            players.RemoveAll(r => r != null && r.steamId == steamId);
        }
    }

    internal static class SimpleJsonCompat
    {
        public static string ToJson(IntroConfig c)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            AppendBool(sb, "enabled", c.enabled, true);
            AppendBool(sb, "autoOpenOnSpawn", c.autoOpenOnSpawn, true);
            AppendBool(sb, "showOnlyFirstJoin", c.showOnlyFirstJoin, true);
            AppendBool(sb, "enableResetCommand", c.enableResetCommand, true);
            AppendString(sb, "title", c.title, true);
            AppendString(sb, "subtitle", c.subtitle, true);
            AppendString(sb, "welcomeText", c.welcomeText, true);
            AppendString(sb, "rulesTitle", c.rulesTitle, true);
            AppendString(sb, "rulesText", c.rulesText, true);
            AppendString(sb, "website", c.website, true);
            AppendString(sb, "discord", c.discord, true);
            AppendString(sb, "enterButtonText", c.enterButtonText, true);
            AppendString(sb, "rulesButtonText", c.rulesButtonText, true);
            AppendString(sb, "closeButtonText", c.closeButtonText, true);
            AppendString(sb, "enterChatMessage", c.enterChatMessage, true);
            AppendString(sb, "adMessage1", c.adMessage1, true);
            AppendString(sb, "adMessage2", c.adMessage2, true);
            AppendString(sb, "adMessage3", c.adMessage3, false);
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string ToJson(SeenPlayersDatabase db)
        {
            if (db == null)
            {
                db = new SeenPlayersDatabase();
            }
            if (db.players == null)
            {
                db.players = new List<SeenPlayerRecord>();
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"players\": [");
            for (int i = 0; i < db.players.Count; i++)
            {
                SeenPlayerRecord r = db.players[i];
                if (r == null)
                {
                    continue;
                }
                sb.AppendLine("    {");
                sb.Append("      "); AppendStringInline(sb, "steamId", r.steamId, true);
                sb.Append("      "); AppendStringInline(sb, "name", r.name, true);
                sb.Append("      "); AppendBoolInline(sb, "seenIntro", r.seenIntro, true);
                sb.Append("      "); AppendStringInline(sb, "firstSeen", r.firstSeen, true);
                sb.Append("      "); AppendStringInline(sb, "lastSeen", r.lastSeen, false);
                sb.Append("    }");
                if (i < db.players.Count - 1)
                {
                    sb.Append(",");
                }
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static IntroConfig FromJsonConfig(string json)
        {
            IntroConfig c = IntroConfig.CreateDefault();
            if (string.IsNullOrEmpty(json))
            {
                return c;
            }
            c.enabled = GetBool(json, "enabled", c.enabled);
            c.autoOpenOnSpawn = GetBool(json, "autoOpenOnSpawn", c.autoOpenOnSpawn);
            c.showOnlyFirstJoin = GetBool(json, "showOnlyFirstJoin", c.showOnlyFirstJoin);
            c.enableResetCommand = GetBool(json, "enableResetCommand", c.enableResetCommand);
            c.title = GetString(json, "title", c.title);
            c.subtitle = GetString(json, "subtitle", c.subtitle);
            c.welcomeText = GetString(json, "welcomeText", c.welcomeText);
            c.rulesTitle = GetString(json, "rulesTitle", c.rulesTitle);
            c.rulesText = GetString(json, "rulesText", c.rulesText);
            c.website = GetString(json, "website", c.website);
            c.discord = GetString(json, "discord", c.discord);
            c.enterButtonText = GetString(json, "enterButtonText", c.enterButtonText);
            c.rulesButtonText = GetString(json, "rulesButtonText", c.rulesButtonText);
            c.closeButtonText = GetString(json, "closeButtonText", c.closeButtonText);
            c.enterChatMessage = GetString(json, "enterChatMessage", c.enterChatMessage);
            c.adMessage1 = GetString(json, "adMessage1", c.adMessage1);
            c.adMessage2 = GetString(json, "adMessage2", c.adMessage2);
            c.adMessage3 = GetString(json, "adMessage3", c.adMessage3);
            return c;
        }

        public static SeenPlayersDatabase FromJsonSeenPlayers(string json)
        {
            SeenPlayersDatabase db = new SeenPlayersDatabase();
            if (string.IsNullOrEmpty(json))
            {
                return db;
            }
            MatchCollection matches = Regex.Matches(json, "\\{[^\\{\\}]*\\}");
            foreach (Match m in matches)
            {
                string value = m.Value;
                if (value.Contains("\"steamId\""))
                {
                    SeenPlayerRecord r = new SeenPlayerRecord();
                    r.steamId = GetString(value, "steamId", "");
                    r.name = GetString(value, "name", "Joueur");
                    r.seenIntro = GetBool(value, "seenIntro", true);
                    r.firstSeen = GetString(value, "firstSeen", "");
                    r.lastSeen = GetString(value, "lastSeen", "");
                    if (!string.IsNullOrEmpty(r.steamId))
                    {
                        db.players.Add(r);
                    }
                }
            }
            return db;
        }

        private static void AppendString(StringBuilder sb, string key, string value, bool comma)
        {
            sb.Append("  ");
            AppendStringInline(sb, key, value, comma);
        }

        private static void AppendBool(StringBuilder sb, string key, bool value, bool comma)
        {
            sb.Append("  ");
            AppendBoolInline(sb, key, value, comma);
        }

        private static void AppendStringInline(StringBuilder sb, string key, string value, bool comma)
        {
            sb.Append("\"").Append(Escape(key)).Append("\": \"").Append(Escape(value)).Append("\"");
            if (comma)
            {
                sb.Append(",");
            }
            sb.AppendLine();
        }

        private static void AppendBoolInline(StringBuilder sb, string key, bool value, bool comma)
        {
            sb.Append("\"").Append(Escape(key)).Append("\": ").Append(value ? "true" : "false");
            if (comma)
            {
                sb.Append(",");
            }
            sb.AppendLine();
        }

        private static string GetString(string json, string key, string defaultValue)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
            if (!m.Success)
            {
                return defaultValue;
            }
            return Unescape(m.Groups["v"].Value);
        }

        private static bool GetBool(string json, string key, bool defaultValue)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return defaultValue;
            }
            return string.Equals(m.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return "";
            }
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Unescape(string value)
        {
            if (value == null)
            {
                return "";
            }
            return value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
