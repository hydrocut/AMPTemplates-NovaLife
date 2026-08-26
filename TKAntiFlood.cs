using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using Life.Network;
using Mirror;
using UnityEngine;

/// <summary>
/// TKAntiFlood v1.2 — TeamKit.fr
///
/// Protège le serveur contre les floods de connexions TCP (attaques type
/// "possible header attack with a header of: 0 bytes" en rafale depuis une
/// même IP) qui saturent le serveur et spamment la console.
///
/// Principe : s'insère dans Transport.active.OnServerConnected, AVANT que
/// Mirror et Nova-Life ne voient la connexion.
///  - Compte les tentatives de connexion par IP sur une fenêtre glissante.
///  - Au-delà du seuil, l'IP est bannie : ses connexions sont coupées
///    immédiatement sans jamais atteindre le jeu.
///  - Les sockets déjà mortes (ObjectDisposedException dans
///    LifeNetworkManager.OnServerConnect) sont coupées silencieusement,
///    ce qui élimine aussi le spam d'exceptions dans la console.
///
/// Bannissements persistés dans Plugins/TKAntiFlood/banned.txt
/// (format : ip;expiration_unix — 0 = permanent). Les IP y sont rechargées
/// au démarrage. Ce fichier peut aussi alimenter un script pare-feu côté
/// hôte (iptables) pour bloquer en amont du process.
///
/// Console :
///   [TKFLOOD] BAN ip=X attempts=N window=Ss     -> nouvelle IP bannie
///   [TKFLOOD] BLOCKED ip=X total=N              -> rappel périodique (throttlé)
/// </summary>
public class TKAntiFlood : Plugin
{
    private TKAntiFloodConfig config;
    private string pluginDir;
    private string bannedFilePath;

    // ip -> timestamps (secondes Unix) des tentatives récentes
    private readonly Dictionary<string, List<double>> attempts = new Dictionary<string, List<double>>();
    // ip -> expiration du ban en secondes Unix (0 = permanent)
    private readonly Dictionary<string, double> banned = new Dictionary<string, double>();
    // ip -> nombre de connexions bloquées depuis le ban (pour le log throttlé)
    private readonly Dictionary<string, long> blockedCount = new Dictionary<string, long>();
    // ip -> dernier log de blocage (pour throttler)
    private readonly Dictionary<string, double> lastBlockLog = new Dictionary<string, double>();

    private HashSet<string> whitelist = new HashSet<string>();
    private bool hooked;
    private double lastPrune;

    public TKAntiFlood(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        LoadBans();
        HookTransport();
        Debug.Log("[TKFLOOD] Plugin TKAntiFlood v1.2 initialisé (seuil "
            + config.maxAttempts + " connexions / " + config.windowSeconds + "s, ban "
            + (config.banMinutes <= 0 ? "permanent" : config.banMinutes + " min") + ")");
    }

    private static double Now()
    {
        return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    // ------------------------------------------------------------------
    // Hook transport : filtre les connexions avant Mirror / Nova-Life
    // ------------------------------------------------------------------
    private void HookTransport()
    {
        if (hooked)
        {
            return;
        }
        try
        {
            Transport transport = Transport.active;
            if (transport == null)
            {
                Debug.LogError("[TKFLOOD] Transport.active introuvable, protection inactive");
                return;
            }

            Action<int> original = transport.OnServerConnected;
            transport.OnServerConnected = delegate(int connectionId)
            {
                if (!FilterConnection(transport, connectionId))
                {
                    return; // connexion coupée : Mirror/Nova-Life ne la voient jamais
                }
                if (original != null)
                {
                    original(connectionId);
                }
            };
            hooked = true;
            Debug.Log("[TKFLOOD] Filtre de connexions installé sur " + transport.GetType().Name);
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFLOOD] Impossible d'installer le filtre : " + ex.Message);
        }
    }

    // Renvoie true si la connexion doit être transmise au jeu.
    private bool FilterConnection(Transport transport, int connectionId)
    {
        MaybeReloadConfig();
        if (!config.enabled)
        {
            return true;
        }
        if (config == null || !config.enabled)
        {
            return true;
        }

        string ip;
        try
        {
            ip = ExtractIp(transport.ServerGetClientAddress(connectionId));
        }
        catch
        {
            // Socket déjà fermée/disposée : connexion morte typique d'un flood.
            // On coupe sans transmettre -> plus d'ObjectDisposedException dans
            // LifeNetworkManager.OnServerConnect.
            SafeDisconnect(transport, connectionId);
            return false;
        }

        if (string.IsNullOrEmpty(ip) || whitelist.Contains(ip))
        {
            return true;
        }

        double now = Now();
        PrunePeriodically(now);

        // IP déjà bannie -> on coupe direct
        if (IsBanned(ip, now))
        {
            SafeDisconnect(transport, connectionId);
            CountBlocked(ip, now);
            return false;
        }

        // Fenêtre glissante des tentatives
        List<double> list;
        if (!attempts.TryGetValue(ip, out list))
        {
            list = new List<double>();
            attempts[ip] = list;
        }
        list.Add(now);
        double windowStart = now - config.windowSeconds;
        list.RemoveAll(delegate(double t) { return t < windowStart; });

        if (list.Count > config.maxAttempts)
        {
            Ban(ip, now, list.Count);
            SafeDisconnect(transport, connectionId);
            return false;
        }

        return true;
    }

    private static void SafeDisconnect(Transport transport, int connectionId)
    {
        try
        {
            transport.ServerDisconnect(connectionId);
        }
        catch
        {
            // déjà déconnectée : rien à faire
        }
    }

    // "[::ffff:195.110.34.240]:49120" / "[2001:db8::1]:123" / "1.2.3.4:56" -> IP nue
    private static string ExtractIp(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return "";
        }
        string ip = address.Trim();
        if (ip.StartsWith("["))
        {
            int end = ip.IndexOf(']');
            if (end > 0)
            {
                ip = ip.Substring(1, end - 1);
            }
        }
        else
        {
            int colon = ip.LastIndexOf(':');
            if (colon > 0 && ip.IndexOf(':') == colon)
            {
                ip = ip.Substring(0, colon); // IPv4:port
            }
        }
        if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
        {
            ip = ip.Substring(7); // IPv4 mappée en IPv6
        }
        return ip;
    }

    // ------------------------------------------------------------------
    // Bans
    // ------------------------------------------------------------------
    private bool IsBanned(string ip, double now)
    {
        double expiry;
        if (!banned.TryGetValue(ip, out expiry))
        {
            return false;
        }
        if (expiry > 0 && expiry < now)
        {
            banned.Remove(ip);
            blockedCount.Remove(ip);
            lastBlockLog.Remove(ip);
            SaveBans();
            Debug.Log("[TKFLOOD] UNBAN ip=" + ip + " (expiration)");
            return false;
        }
        return true;
    }

    private void Ban(string ip, double now, int attemptCount)
    {
        double expiry = config.banMinutes <= 0 ? 0 : now + config.banMinutes * 60.0;
        banned[ip] = expiry;
        attempts.Remove(ip);
        SaveBans();
        Debug.Log("[TKFLOOD] BAN ip=" + ip + " attempts=" + attemptCount
            + " window=" + config.windowSeconds + "s"
            + (expiry == 0 ? " (permanent)" : " (" + config.banMinutes + " min)"));
    }

    private void CountBlocked(string ip, double now)
    {
        long total;
        blockedCount.TryGetValue(ip, out total);
        total++;
        blockedCount[ip] = total;

        double last;
        lastBlockLog.TryGetValue(ip, out last);
        if (now - last >= config.blockLogIntervalSeconds)
        {
            lastBlockLog[ip] = now;
            Debug.Log("[TKFLOOD] BLOCKED ip=" + ip + " total=" + total);
        }
    }

    // Purge périodique pour ne pas accumuler de la mémoire pendant une attaque
    private void PrunePeriodically(double now)
    {
        if (now - lastPrune < 60)
        {
            return;
        }
        lastPrune = now;
        double windowStart = now - config.windowSeconds;
        List<string> stale = new List<string>();
        foreach (KeyValuePair<string, List<double>> kv in attempts)
        {
            kv.Value.RemoveAll(delegate(double t) { return t < windowStart; });
            if (kv.Value.Count == 0)
            {
                stale.Add(kv.Key);
            }
        }
        foreach (string ip in stale)
        {
            attempts.Remove(ip);
        }
    }

    // ------------------------------------------------------------------
    // Persistance : banned.txt ("ip;expirationUnix", 0 = permanent)
    // ------------------------------------------------------------------
    private void LoadBans()
    {
        try
        {
            if (!File.Exists(bannedFilePath))
            {
                return;
            }
            double now = Now();
            int loaded = 0;
            foreach (string raw in File.ReadAllLines(bannedFilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                string[] parts = line.Split(';');
                string ip = parts[0].Trim();
                double expiry = 0;
                if (parts.Length > 1)
                {
                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out expiry);
                }
                if (ip.Length == 0 || (expiry > 0 && expiry < now))
                {
                    continue; // expiré
                }
                banned[ip] = expiry;
                loaded++;
            }
            if (loaded > 0)
            {
                Debug.Log("[TKFLOOD] " + loaded + " IP bannie(s) rechargée(s) depuis banned.txt");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFLOOD] Erreur lecture banned.txt : " + ex.Message);
        }
    }

    private void SaveBans()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# TKAntiFlood — IP bannies (ip;expiration_unix, 0 = permanent)");
            sb.AppendLine("# Supprimer une ligne + redémarrer le serveur pour débannir.");
            foreach (KeyValuePair<string, double> kv in banned)
            {
                sb.AppendLine(kv.Key + ";" + kv.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            }
            File.WriteAllText(bannedFilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFLOOD] Erreur écriture banned.txt : " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Config
    // ------------------------------------------------------------------
    private string configPathSaved;
    private string lastConfigJson;
    private string lastBansText;
    private int lastReloadTick;

    // Vérifie (au plus toutes les 20 s) si config.json a changé (panel web) — à chaud.
    private void MaybeReloadConfig()
    {
        int tick = Environment.TickCount;
        if (lastReloadTick != 0 && unchecked(tick - lastReloadTick) < 20000)
        {
            return;
        }
        lastReloadTick = tick == 0 ? 1 : tick;
        try
        {
            if (configPathSaved == null || !File.Exists(configPathSaved))
            {
                return;
            }
            ReloadBansIfChanged();
            string txt = File.ReadAllText(configPathSaved);
            if (txt == lastConfigJson)
            {
                return;
            }
            lastConfigJson = txt;
            config = TKAntiFloodConfig.FromJson(txt);
            whitelist = new HashSet<string>();
            whitelist.Add("127.0.0.1");
            whitelist.Add("::1");
            if (!string.IsNullOrEmpty(config.whitelist))
            {
                foreach (string entry in config.whitelist.Split(','))
                {
                    string ip = entry.Trim();
                    if (ip.Length > 0)
                    {
                        whitelist.Add(ip);
                    }
                }
            }
            Debug.Log("[TKFLOOD] Config rechargée (panel) : actif=" + config.enabled
                + ", seuil " + config.maxAttempts + "/" + config.windowSeconds + "s, ban "
                + (config.banMinutes <= 0 ? "permanent" : config.banMinutes + " min"));
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFLOOD] Erreur rechargement config : " + ex.Message);
        }
    }

    // Recharge banned.txt s'il a changé (ban IP ajouté depuis le panel)
    private void ReloadBansIfChanged()
    {
        try
        {
            if (bannedFilePath == null || !File.Exists(bannedFilePath))
            {
                return;
            }
            string txt = File.ReadAllText(bannedFilePath);
            if (txt == lastBansText)
            {
                return;
            }
            bool first = lastBansText == null;
            lastBansText = txt;
            if (first)
            {
                return; // premier passage : les bans sont déjà chargés au boot
            }
            banned.Clear();
            LoadBans();
            Debug.Log("[TKFLOOD] banned.txt rechargé (" + banned.Count + " IP)");
        }
        catch
        {
        }
    }

    private void LoadConfig()
    {
        try
        {
            pluginDir = Path.Combine(pluginsPath, "TKAntiFlood");
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
            }
            bannedFilePath = Path.Combine(pluginDir, "banned.txt");
            string configPath = Path.Combine(pluginDir, "config.json");
            if (!File.Exists(configPath))
            {
                config = new TKAntiFloodConfig();
                File.WriteAllText(configPath, TKAntiFloodConfig.ToJson(config));
                configPathSaved = configPath;
                lastConfigJson = TKAntiFloodConfig.ToJson(config);
                Debug.Log("[TKFLOOD] config.json créé : " + configPath);
            }
            else
            {
                config = TKAntiFloodConfig.FromJson(File.ReadAllText(configPath));
                File.WriteAllText(configPath, TKAntiFloodConfig.ToJson(config));
                configPathSaved = configPath;
                lastConfigJson = TKAntiFloodConfig.ToJson(config);
                Debug.Log("[TKFLOOD] config.json chargé");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFLOOD] Erreur chargement config, valeurs par défaut : " + ex.Message);
            config = new TKAntiFloodConfig();
        }

        whitelist = new HashSet<string>();
        whitelist.Add("127.0.0.1");
        whitelist.Add("::1");
        if (!string.IsNullOrEmpty(config.whitelist))
        {
            foreach (string entry in config.whitelist.Split(','))
            {
                string ip = entry.Trim();
                if (ip.Length > 0)
                {
                    whitelist.Add(ip);
                }
            }
        }
    }
}

[Serializable]
public class TKAntiFloodConfig
{
    public bool enabled = true;
    // Nombre max de tentatives de connexion par IP sur la fenêtre.
    // Un joueur normal se connecte 1 fois ; 8 en 20 s = flood évident.
    public int maxAttempts = 8;
    public int windowSeconds = 20;
    // Durée du ban en minutes. 0 = permanent (jusqu'à retrait de banned.txt).
    public int banMinutes = 720;
    // IP jamais bloquées, séparées par des virgules (127.0.0.1 et ::1 toujours inclus)
    public string whitelist = "";
    // Intervalle mini entre deux logs "BLOCKED" pour une même IP (anti-spam console)
    public int blockLogIntervalSeconds = 30;

    public static string ToJson(TKAntiFloodConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"maxAttempts\": " + c.maxAttempts + ",");
        sb.AppendLine("  \"windowSeconds\": " + c.windowSeconds + ",");
        sb.AppendLine("  \"banMinutes\": " + c.banMinutes + ",");
        sb.AppendLine("  \"whitelist\": \"" + Escape(c.whitelist) + "\",");
        sb.AppendLine("  \"blockLogIntervalSeconds\": " + c.blockLogIntervalSeconds);
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKAntiFloodConfig FromJson(string json)
    {
        TKAntiFloodConfig c = new TKAntiFloodConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = GetBool(json, "enabled", c.enabled);
        c.maxAttempts = GetInt(json, "maxAttempts", c.maxAttempts);
        c.windowSeconds = GetInt(json, "windowSeconds", c.windowSeconds);
        c.banMinutes = GetInt(json, "banMinutes", c.banMinutes);
        c.whitelist = GetString(json, "whitelist", c.whitelist);
        c.blockLogIntervalSeconds = GetInt(json, "blockLogIntervalSeconds", c.blockLogIntervalSeconds);
        if (c.maxAttempts < 2) c.maxAttempts = 2;
        if (c.windowSeconds < 1) c.windowSeconds = 1;
        if (c.blockLogIntervalSeconds < 5) c.blockLogIntervalSeconds = 5;
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

    private static int GetInt(string json, string key, int defaultValue)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+)");
        if (!m.Success)
        {
            return defaultValue;
        }
        int value;
        return int.TryParse(m.Groups["v"].Value, out value) ? value : defaultValue;
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
