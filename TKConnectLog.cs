using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using UnityEngine;

/// <summary>
/// TKConnectLog v2.1 — TeamKit.fr
///
/// 1) Écrit dans la console serveur des lignes normalisées à la connexion
///    et à la déconnexion des joueurs, pour qu'AMP affiche les pseudos
///    et suive les entrées/sorties :
///      [TKLOG] JOIN pseudo="PseudoSteam" steamid=76561198000000000
///      [TKLOG] LEAVE pseudo="PseudoSteam" steamid=76561198000000000
///
/// 2) Annonce en jeu les arrivées/départs et souhaite la bienvenue
///    aux joueurs (textes et lien Discord configurables via
///    Plugins/TKConnectLog/config.json — gérable depuis le panel AMP).
///
/// v2.1 :
///  - Couleurs configurables (accentColor / textColor)
/// v2.0 :
///  - Configuration via config.json (Discord, messages, activation)
///  - Nettoyage des caractères invisibles dans les pseudos Steam
/// </summary>
public class TKConnectLog : Plugin
{
    private TKConnectConfig config;
    private string configPath;

    private class PlayerInfo
    {
        public string pseudo;
        public string logLine;
    }

    // connectionId -> infos joueur, pour retrouver le pseudo à la déconnexion
    private readonly Dictionary<int, PlayerInfo> connectedPlayers = new Dictionary<int, PlayerInfo>();

    private bool disconnectHooked;

    public TKConnectLog(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        HookDisconnectEvent();
        Debug.Log("[TKLOG] Plugin TKConnectLog v2.2 initialisé");
    }

    private void LoadConfig()
    {
        try
        {
            string dir = Path.Combine(pluginsPath, "TKConnectLog");
            configPath = Path.Combine(dir, "config.json");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (!File.Exists(configPath))
            {
                config = new TKConnectConfig();
                File.WriteAllText(configPath, TKConnectConfig.ToJson(config));
                Debug.Log("[TKLOG] config.json créé : " + configPath);
            }
            else
            {
                config = TKConnectConfig.FromJson(File.ReadAllText(configPath));
                // Migration : réécrit le fichier avec toutes les clés pour que
                // l'AutoMap AMP puisse écrire les nouveaux champs (couleurs...)
                File.WriteAllText(configPath, TKConnectConfig.ToJson(config));
                Debug.Log("[TKLOG] config.json chargé");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKLOG] Erreur chargement config, valeurs par défaut : " + ex.Message);
            config = new TKConnectConfig();
        }
    }

    // Le jeu n'appelle jamais le hook plugin OnPlayerDisconnect : on se branche
    // directement sur l'événement public du serveur.
    private void HookDisconnectEvent()
    {
        if (disconnectHooked)
        {
            return;
        }
        try
        {
            if (Nova.server != null)
            {
                Nova.server.OnPlayerDisconnectEvent += HandleDisconnect;
                disconnectHooked = true;
                Debug.Log("[TKLOG] Événement de déconnexion branché");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKLOG] Impossible de brancher l'événement de déconnexion : " + ex.Message);
        }
    }

    // Retire les caractères de contrôle et invisibles (zéro-largeur, BOM...)
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        StringBuilder sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                continue;
            }
            if (c >= '​' && c <= '‏')
            {
                continue;
            }
            if (c == '﻿' || c == '⁠')
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static string ResolvePseudo(Player player, Characters character)
    {
        // steamUsername peut être vide ou polluée au moment du spawn
        string pseudo = Sanitize(player.steamUsername);
        if (pseudo.Length == 0)
        {
            try { pseudo = Sanitize(player.FullName); } catch { }
        }
        if (pseudo.Length == 0 && character != null)
        {
            try { pseudo = Sanitize(character.Firstname + " " + character.Lastname); } catch { }
        }
        if (pseudo.Length == 0)
        {
            pseudo = "Joueur " + player.steamId;
        }
        return pseudo;
    }

    public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
    {
        base.OnPlayerSpawnCharacter(player, conn, character);
        HookDisconnectEvent();

        string pseudo = ResolvePseudo(player, character);
        string logLine = $"pseudo=\"{pseudo}\" steamid={player.steamId}";

        ApplyAdminLevel(player, pseudo);

        // Ne traite le JOIN qu'une fois par connexion (pas à chaque changement de personnage)
        if (!connectedPlayers.ContainsKey(conn.connectionId))
        {
            connectedPlayers[conn.connectionId] = new PlayerInfo
            {
                pseudo = pseudo,
                logLine = logLine
            };

            // Ligne console pour AMP
            Debug.Log($"[TKLOG] JOIN {logLine}");

            if (config.enabled)
            {
                // Annonce à tous les joueurs
                if (config.showJoinLeave)
                {
                    Nova.server.SendMessageToAll(FormatBroadcast(config.joinMessage, pseudo));
                }

                // Messages de bienvenue au joueur qui arrive
                if (!string.IsNullOrEmpty(config.welcomeTitle))
                {
                    player.SendText("<color=" + Accent() + ">" + config.welcomeTitle + "</color>");
                }
                if (!string.IsNullOrEmpty(config.hostedByText))
                {
                    player.SendText("<color=" + TextCol() + ">" + config.hostedByText + "</color>");
                }
                if (!string.IsNullOrEmpty(config.discord))
                {
                    player.SendText("<color=" + Accent() + ">Discord :</color> " + config.discord);
                }
            }
        }
        else
        {
            connectedPlayers[conn.connectionId].pseudo = pseudo;
            connectedPlayers[conn.connectionId].logLine = logLine;
        }
    }

    // Applique le niveau admin configuré depuis AMP (format : "steamid:niveau,steamid:niveau")
    private void ApplyAdminLevel(Player player, string pseudo)
    {
        if (string.IsNullOrEmpty(config.adminSteamIds))
        {
            return;
        }
        try
        {
            string steamId = player.steamId.ToString();
            foreach (string entry in config.adminSteamIds.Split(','))
            {
                string[] parts = entry.Trim().Split(':');
                if (parts.Length == 0 || parts[0].Trim() != steamId)
                {
                    continue;
                }
                int level = 5;
                if (parts.Length > 1)
                {
                    int.TryParse(parts[1].Trim(), out level);
                }
                string pin = parts.Length > 2 ? parts[2].Trim() : null;
                bool changed = false;
                if (player.account.adminLevel != level)
                {
                    player.account.adminLevel = level;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(pin) && player.account.adminPin != pin)
                {
                    player.account.adminPin = pin;
                    changed = true;
                }
                if (changed)
                {
                    _ = Life.DB.LifeDB.SaveAccount(player.account);
                    Debug.Log($"[TKLOG] ADMIN niveau {level}{(pin != null ? " + PIN" : "")} appliqué à pseudo=\"{pseudo}\" steamid={steamId}");
                    player.SendText("<color=" + Accent() + ">Niveau admin " + level + " appliqué (config AMP).</color>");
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKLOG] Erreur application admin : " + ex.Message);
        }
    }

    // Garde le hook officiel au cas où le jeu le câblerait un jour :
    // HandleDisconnect retire le joueur du dictionnaire, donc pas de doublon possible.
    public override void OnPlayerDisconnect(NetworkConnection conn)
    {
        base.OnPlayerDisconnect(conn);
        HandleDisconnect(conn);
    }

    private void HandleDisconnect(NetworkConnection conn)
    {
        if (conn == null)
        {
            return;
        }

        if (connectedPlayers.TryGetValue(conn.connectionId, out PlayerInfo info))
        {
            connectedPlayers.Remove(conn.connectionId);

            // Ligne console pour AMP
            Debug.Log($"[TKLOG] LEAVE {info.logLine}");

            if (config.enabled && config.showJoinLeave)
            {
                try
                {
                    Nova.server.SendMessageToAll(FormatBroadcast(config.leaveMessage, info.pseudo));
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TKLOG] Erreur annonce départ : " + ex.Message);
                }
            }
        }
    }

    private string Accent()
    {
        return "#" + (string.IsNullOrEmpty(config.accentColor) ? "00f0ff" : config.accentColor.TrimStart('#'));
    }

    private string TextCol()
    {
        return "#" + (string.IsNullOrEmpty(config.textColor) ? "b8b8c8" : config.textColor.TrimStart('#'));
    }

    private string FormatBroadcast(string template, string pseudo)
    {
        if (string.IsNullOrEmpty(template))
        {
            template = "{pseudo}";
        }
        return "<color=" + TextCol() + ">" + template.Replace("{pseudo}", "</color><color=" + Accent() + ">" + pseudo + "</color><color=" + TextCol() + ">") + "</color>";
    }
}

[Serializable]
public class TKConnectConfig
{
    public bool enabled = true;
    public bool showJoinLeave = true;
    public string welcomeTitle = "Bienvenue sur le serveur !";
    public string hostedByText = "Serveur hébergé gratuitement par TeamKit.fr";
    public string discord = "https://discord.gg/JXAxAupBqz";
    public string joinMessage = "{pseudo} a rejoint le serveur";
    public string leaveMessage = "{pseudo} a quitté le serveur";
    public string accentColor = "00f0ff";
    public string textColor = "b8b8c8";
    public string adminSteamIds = "";

    public static string ToJson(TKConnectConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"showJoinLeave\": " + (c.showJoinLeave ? "true" : "false") + ",");
        sb.AppendLine("  \"welcomeTitle\": \"" + Escape(c.welcomeTitle) + "\",");
        sb.AppendLine("  \"hostedByText\": \"" + Escape(c.hostedByText) + "\",");
        sb.AppendLine("  \"discord\": \"" + Escape(c.discord) + "\",");
        sb.AppendLine("  \"joinMessage\": \"" + Escape(c.joinMessage) + "\",");
        sb.AppendLine("  \"leaveMessage\": \"" + Escape(c.leaveMessage) + "\",");
        sb.AppendLine("  \"accentColor\": \"" + Escape(c.accentColor) + "\",");
        sb.AppendLine("  \"textColor\": \"" + Escape(c.textColor) + "\",");
        sb.AppendLine("  \"adminSteamIds\": \"" + Escape(c.adminSteamIds) + "\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKConnectConfig FromJson(string json)
    {
        TKConnectConfig c = new TKConnectConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = GetBool(json, "enabled", c.enabled);
        c.showJoinLeave = GetBool(json, "showJoinLeave", c.showJoinLeave);
        c.welcomeTitle = GetString(json, "welcomeTitle", c.welcomeTitle);
        c.hostedByText = GetString(json, "hostedByText", c.hostedByText);
        c.discord = GetString(json, "discord", c.discord);
        c.joinMessage = GetString(json, "joinMessage", c.joinMessage);
        c.leaveMessage = GetString(json, "leaveMessage", c.leaveMessage);
        c.accentColor = GetString(json, "accentColor", c.accentColor);
        c.textColor = GetString(json, "textColor", c.textColor);
        c.adminSteamIds = GetString(json, "adminSteamIds", c.adminSteamIds);
        return c;
    }

    private static string GetString(string json, string key, string defaultValue)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
        if (!m.Success)
        {
            return defaultValue;
        }
        return m.Groups["v"].Value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
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
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
    }
}
