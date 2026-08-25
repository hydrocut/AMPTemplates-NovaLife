using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using Life.Network;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKAntiCheat v1.0 — TeamKit.fr
///
/// Anti-cheat serveur "de base" pour Nova-Life. MODE ALERTE UNIQUEMENT :
/// il détecte et signale, il ne sanctionne jamais automatiquement (aucun
/// kick/ban auto — les faux positifs ne pénalisent aucun joueur légitime).
///
/// Détections :
///  1. ARGENT — gain d'espèces ou de banque supérieur à un seuil en une
///     seule transaction, hors raisons légitimes (whitelist de mots-clés :
///     admin, salaire, vente, banque...). Détecte les injections d'argent.
///  2. VITESSE / TÉLÉPORT — vitesse au sol anormale (m/s) hors véhicule,
///     confirmée sur 2 relevés consécutifs pour éviter le lag. Ignore le
///     premier relevé après connexion / mort / changement de perso
///     (téléports légitimes).
///
/// Les détections sont : loggées en console AMP ([TKAC] ...), gardées en
/// mémoire (exposées au panel TKWebPanel via le fichier partagé
/// Plugins/TKAntiCheat/alerts.json) — le panel affiche l'historique.
///
/// Config : Plugins/TKAntiCheat/config.json
/// </summary>
public class TKAntiCheat : Plugin
{
    private TKAntiCheatConfig config;
    private string pluginDir;
    private string alertsPath;

    private bool hooked;

    private class Track
    {
        public Vector3 lastPos;
        public float lastTime;
        public bool hasPos;
        public int overSpeedStreak;
        public float ignoreUntil; // téléport légitime : on ignore la vitesse un instant
    }

    private readonly Dictionary<ulong, Track> tracks = new Dictionary<ulong, Track>();
    // Historique récent (pour le panel + throttle des logs identiques)
    private readonly List<string> alerts = new List<string>();

    public TKAntiCheat(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        if (!config.enabled)
        {
            Debug.Log("[TKAC] Plugin TKAntiCheat v1.0 désactivé par config");
            return;
        }
        HookEvents();
        try
        {
            GameObject go = new GameObject("TKAntiCheat");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TKAntiCheatTicker ticker = go.AddComponent<TKAntiCheatTicker>();
            ticker.plugin = this;
            ticker.intervalSeconds = config.checkIntervalSeconds;
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Impossible de démarrer le ticker : " + ex.Message);
        }
        Debug.Log("[TKAC] Plugin TKAntiCheat v1.0 initialisé (ALERTE seule — argent > "
            + config.moneyAlertThreshold.ToString("0") + " / vitesse > " + config.maxSpeed + " m/s)");
    }

    private void HookEvents()
    {
        if (hooked || Nova.server == null)
        {
            return;
        }
        try
        {
            Nova.server.OnPlayerMoneyEvent += delegate (Player p, double amount, string reason) { OnMoney(p, amount, reason, false); };
            Nova.server.OnPlayerBankEvent += delegate (Player p, double amount, string reason) { OnMoney(p, amount, reason, true); };
            Nova.server.OnPlayerConnectEvent += delegate (Player p) { MarkTeleport(p); };
            Nova.server.OnPlayerSpawnCharacterEvent += delegate (Player p) { MarkTeleport(p); };
            Nova.server.OnPlayerDeathEvent += delegate (Player p) { MarkTeleport(p); };
            hooked = true;
            Debug.Log("[TKAC] Événements branchés (argent, connexion, spawn, mort)");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur branchement événements : " + ex.Message);
        }
    }

    // Un téléport légitime vient de se produire : on met en pause la détection
    // de vitesse pour ce joueur quelques secondes.
    private void MarkTeleport(Player p)
    {
        if (p == null)
        {
            return;
        }
        Track t = GetTrack(p.steamId);
        t.hasPos = false;
        t.overSpeedStreak = 0;
        t.ignoreUntil = Time.realtimeSinceStartup + config.teleportGraceSeconds;
    }

    private Track GetTrack(ulong steamId)
    {
        Track t;
        if (!tracks.TryGetValue(steamId, out t))
        {
            t = new Track();
            tracks[steamId] = t;
        }
        return t;
    }

    private void OnMoney(Player p, double amount, string reason, bool bank)
    {
        if (p == null || amount <= 0)
        {
            return;
        }
        if (amount < config.moneyAlertThreshold)
        {
            return;
        }
        string r = (reason ?? "").ToLowerInvariant();
        foreach (string w in config.reasonWhitelist)
        {
            if (r.Contains(w))
            {
                return; // gain légitime connu
            }
        }
        string pseudo = SafePseudo(p);
        Alert("ARGENT", pseudo, p.steamId,
            "+" + amount.ToString("0") + (bank ? " banque" : " espèces")
            + " raison=\"" + (reason ?? "") + "\"");
    }

    // Appelé par le ticker sur le thread principal
    public void CheckSpeeds(float now)
    {
        if (Nova.server == null)
        {
            return;
        }
        List<Player> players = Nova.server.GetAllInGamePlayers();
        foreach (Player p in players)
        {
            if (p == null || p.setup == null)
            {
                continue;
            }
            Track t = GetTrack(p.steamId);
            Vector3 pos;
            try { pos = p.setup.transform.position; } catch { continue; }

            if (!t.hasPos)
            {
                t.lastPos = pos;
                t.lastTime = now;
                t.hasPos = true;
                continue;
            }

            float dt = now - t.lastTime;
            t.lastTime = now;
            float dist = Vector3.Distance(pos, t.lastPos);
            t.lastPos = pos;
            if (dt <= 0.01f)
            {
                continue;
            }

            // En véhicule = vitesse légitime, on ignore
            bool inVehicle = false;
            try { inVehicle = p.GetVehicle() != null; } catch { }
            if (inVehicle || now < t.ignoreUntil)
            {
                t.overSpeedStreak = 0;
                continue;
            }

            float speed = dist / dt;
            if (speed > config.maxSpeed)
            {
                t.overSpeedStreak++;
                // Un saut ponctuel = lag/téléport ; on exige la persistance,
                // OU un bond unique énorme (téléport manifeste).
                if (t.overSpeedStreak >= 2 || dist > config.teleportDistance)
                {
                    string pseudo = SafePseudo(p);
                    Alert(dist > config.teleportDistance ? "TELEPORT" : "VITESSE", pseudo, p.steamId,
                        speed.ToString("0") + " m/s (bond " + dist.ToString("0") + " m)");
                    t.overSpeedStreak = 0;
                }
            }
            else
            {
                t.overSpeedStreak = 0;
            }
        }
    }

    private static string SafePseudo(Player p)
    {
        try
        {
            if (!string.IsNullOrEmpty(p.steamUsername))
            {
                return p.steamUsername;
            }
            if (p.character != null)
            {
                return p.character.Firstname + " " + p.character.Lastname;
            }
        }
        catch
        {
        }
        return "Joueur " + p.steamId;
    }

    private void Alert(string kind, string pseudo, ulong steamId, string detail)
    {
        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Debug.LogWarning("[TKAC] " + kind + " — " + pseudo + " (" + steamId + ") : " + detail);

        string json = "{\"time\":" + Json2.Str(time)
            + ",\"kind\":" + Json2.Str(kind)
            + ",\"pseudo\":" + Json2.Str(pseudo)
            + ",\"steamId\":\"" + steamId + "\""
            + ",\"detail\":" + Json2.Str(detail) + "}";
        alerts.Add(json);
        while (alerts.Count > config.maxAlerts)
        {
            alerts.RemoveAt(0);
        }
        SaveAlerts();
    }

    private void SaveAlerts()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            for (int i = alerts.Count - 1; i >= 0; i--) // plus récent en premier
            {
                sb.Append(alerts[i]);
                if (i > 0)
                {
                    sb.Append(",");
                }
            }
            sb.Append("]");
            File.WriteAllText(alertsPath, sb.ToString());
        }
        catch
        {
        }
    }

    private void LoadConfig()
    {
        try
        {
            pluginDir = Path.Combine(pluginsPath, "TKAntiCheat");
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
            }
            alertsPath = Path.Combine(pluginDir, "alerts.json");
            string configPath = Path.Combine(pluginDir, "config.json");
            if (!File.Exists(configPath))
            {
                config = new TKAntiCheatConfig();
                File.WriteAllText(configPath, TKAntiCheatConfig.ToJson(config));
                Debug.Log("[TKAC] config.json créé : " + configPath);
            }
            else
            {
                config = TKAntiCheatConfig.FromJson(File.ReadAllText(configPath));
                File.WriteAllText(configPath, TKAntiCheatConfig.ToJson(config));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur config : " + ex.Message);
            config = new TKAntiCheatConfig();
        }
    }
}

public class TKAntiCheatTicker : MonoBehaviour
{
    public TKAntiCheat plugin;
    public int intervalSeconds = 1;
    private float accum;

    private void Update()
    {
        accum += Time.unscaledDeltaTime;
        if (accum < intervalSeconds)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;
        accum = 0f;
        try
        {
            plugin.CheckSpeeds(now);
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur check : " + ex.Message);
        }
    }
}

[Serializable]
public class TKAntiCheatConfig
{
    public bool enabled = true;
    // Gain d'argent (une transaction) au-delà duquel on alerte
    public double moneyAlertThreshold = 500000;
    // Vitesse au sol max tolérée hors véhicule (m/s). Sprint ~6, cheval ~12.
    public float maxSpeed = 30f;
    // Bond de distance en un relevé = téléport manifeste (m)
    public float teleportDistance = 120f;
    // Grâce après connexion/mort/spawn (s) : on ne juge pas la vitesse
    public float teleportGraceSeconds = 6f;
    // Période de relevé des positions (s)
    public int checkIntervalSeconds = 1;
    // Nb d'alertes gardées en mémoire/fichier
    public int maxAlerts = 200;
    // Raisons de gain d'argent considérées légitimes (sous-chaînes, minuscule)
    public string[] reasonWhitelist = new string[]
    {
        "admin", "salaire", "salary", "vente", "sell", "banque", "bank",
        "retrait", "depot", "dépôt", "virement", "loyer", "impot", "impôt",
        "panel", "caf", "allocation", "facture", "achat", "remboursement"
    };

    public static string ToJson(TKAntiCheatConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"moneyAlertThreshold\": " + c.moneyAlertThreshold.ToString("0") + ",");
        sb.AppendLine("  \"maxSpeed\": " + c.maxSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"teleportDistance\": " + c.teleportDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"teleportGraceSeconds\": " + c.teleportGraceSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"checkIntervalSeconds\": " + c.checkIntervalSeconds + ",");
        sb.AppendLine("  \"maxAlerts\": " + c.maxAlerts + ",");
        sb.Append("  \"reasonWhitelist\": [");
        for (int i = 0; i < c.reasonWhitelist.Length; i++)
        {
            sb.Append(Json2.Str(c.reasonWhitelist[i]));
            if (i < c.reasonWhitelist.Length - 1)
            {
                sb.Append(", ");
            }
        }
        sb.AppendLine("]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKAntiCheatConfig FromJson(string json)
    {
        TKAntiCheatConfig c = new TKAntiCheatConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = GetBool(json, "enabled", c.enabled);
        c.moneyAlertThreshold = GetDouble(json, "moneyAlertThreshold", c.moneyAlertThreshold);
        c.maxSpeed = (float)GetDouble(json, "maxSpeed", c.maxSpeed);
        c.teleportDistance = (float)GetDouble(json, "teleportDistance", c.teleportDistance);
        c.teleportGraceSeconds = (float)GetDouble(json, "teleportGraceSeconds", c.teleportGraceSeconds);
        c.checkIntervalSeconds = (int)GetDouble(json, "checkIntervalSeconds", c.checkIntervalSeconds);
        c.maxAlerts = (int)GetDouble(json, "maxAlerts", c.maxAlerts);
        Match m = Regex.Match(json, "\"reasonWhitelist\"\\s*:\\s*\\[(?<v>[^\\]]*)\\]");
        if (m.Success)
        {
            List<string> words = new List<string>();
            foreach (Match w in Regex.Matches(m.Groups["v"].Value, "\"(?<w>(?:\\\\.|[^\"])*)\""))
            {
                words.Add(w.Groups["w"].Value.ToLowerInvariant());
            }
            if (words.Count > 0)
            {
                c.reasonWhitelist = words.ToArray();
            }
        }
        if (c.checkIntervalSeconds < 1) c.checkIntervalSeconds = 1;
        if (c.maxSpeed < 8) c.maxSpeed = 8;
        return c;
    }

    private static bool GetBool(string json, string key, bool def)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)", RegexOptions.IgnoreCase);
        return m.Success ? string.Equals(m.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase) : def;
    }

    private static double GetDouble(string json, string key, double def)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+(\\.\\d+)?)");
        double v;
        return m.Success && double.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
    }
}

// JSON string encoder autonome (TKWebPanel a le sien, ce plugin est indépendant)
public static class Json2
{
    public static string Str(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }
        StringBuilder sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append("\"");
        return sb.ToString();
    }
}
