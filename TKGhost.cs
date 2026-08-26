using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using Life.Network;
using Life.VehicleSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKGhost v1.1 — TeamKit.fr
///
/// Optimisation FPS client : transforme automatiquement en « fantômes »
/// les véhicules abandonnés dans le monde.
///
/// Un véhicule réel spawné coûte cher pour CHAQUE client (physique,
/// synchronisation réseau, audio). Nova-Life possède nativement un système
/// de véhicules fantômes (FakeVehicle : simple visuel statique, quasi
/// gratuit) et re-matérialise AUTOMATIQUEMENT le véhicule quand un joueur
/// interagit avec (CharacterInteraction -> TryReplaceFakeWithCar). Le
/// concessionnaire fonctionne déjà comme ça.
///
/// Ce plugin applique la conversion aux véhicules laissés dans la rue :
///  - immobile depuis ghostAfterMinutes (pas bougé de plus de 2 m)
///  - aucun joueur dans un rayon de playerRadiusMeters (pas de "pop"
///    visuel sous les yeux de quelqu'un, et couvre les occupants assis)
///  - la conversion passe par TryReplaceCarWithFake SANS force : les
///    garde-fous internes du jeu (capot/coffre ouvert, remorquage...)
///    peuvent refuser, on respecte.
///
/// Aucune perte pour le joueur : son véhicule reste visible au même
/// endroit et redevient réel dès qu'il clique dessus.
///
/// Config : Plugins/TKGhost/config.json — logs : [TKGHOST]
/// </summary>
public class TKGhost : Plugin
{
    public static TKGhostConfig config;
    public static int ghostedSinceBoot;

    public TKGhost(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        if (!config.enabled)
        {
            Debug.Log("[TKGHOST] Plugin TKGhost v1.1 désactivé par config (réactivable depuis le panel)");
        }
        try
        {
            GameObject go = new GameObject("TKGhost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TKGhostTicker ticker = go.AddComponent<TKGhostTicker>();
            ticker.config = config;
            ticker.plugin = this;
            Debug.Log("[TKGHOST] Plugin TKGhost v1.1 initialisé (fantôme après "
                + config.ghostAfterMinutes + " min d'immobilité, rayon joueurs "
                + config.playerRadiusMeters + " m)");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKGHOST] Impossible de démarrer le ticker : " + ex.Message);
        }
    }

    private string configPathSaved;
    private string lastConfigJson;

    // Relit config.json s'il a changé (modifié depuis le panel web) — à chaud.
    public void ReloadConfig()
    {
        try
        {
            if (configPathSaved == null || !File.Exists(configPathSaved))
            {
                return;
            }
            string txt = File.ReadAllText(configPathSaved);
            if (txt == lastConfigJson)
            {
                return;
            }
            lastConfigJson = txt;
            TKGhostConfig c = TKGhostConfig.FromJson(txt);
            config.enabled = c.enabled;
            config.ghostAfterMinutes = c.ghostAfterMinutes;
            config.playerRadiusMeters = c.playerRadiusMeters;
            config.checkIntervalSeconds = c.checkIntervalSeconds;
            config.logChanges = c.logChanges;
            Debug.Log("[TKGHOST] Config rechargée (panel) : actif=" + config.enabled
                + ", fantôme après " + config.ghostAfterMinutes + " min, rayon " + config.playerRadiusMeters + " m");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKGHOST] Erreur rechargement config : " + ex.Message);
        }
    }

    private void LoadConfig()
    {
        try
        {
            string dir = Path.Combine(pluginsPath, "TKGhost");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath))
            {
                config = new TKGhostConfig();
                File.WriteAllText(configPath, TKGhostConfig.ToJson(config));
                configPathSaved = configPath;
                lastConfigJson = TKGhostConfig.ToJson(config);
                Debug.Log("[TKGHOST] config.json créé : " + configPath);
            }
            else
            {
                config = TKGhostConfig.FromJson(File.ReadAllText(configPath));
                File.WriteAllText(configPath, TKGhostConfig.ToJson(config));
                configPathSaved = configPath;
                lastConfigJson = TKGhostConfig.ToJson(config);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKGHOST] Erreur config : " + ex.Message);
            config = new TKGhostConfig();
        }
    }
}

public class TKGhostTicker : MonoBehaviour
{
    public TKGhostConfig config;
    public TKGhost plugin;
    private float reloadAccum;

    private class Track
    {
        public Vector3 lastPos;
        public float stillSince;
    }

    private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
    private float accum;

    private void Update()
    {
        if (config == null)
        {
            return;
        }
        reloadAccum += Time.unscaledDeltaTime;
        if (reloadAccum >= 20f)
        {
            reloadAccum = 0f;
            if (plugin != null)
            {
                plugin.ReloadConfig();
            }
        }
        if (!config.enabled)
        {
            return;
        }
        accum += Time.unscaledDeltaTime;
        if (accum < config.checkIntervalSeconds)
        {
            return;
        }
        accum = 0f;
        try
        {
            Sweep();
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKGHOST] Erreur sweep : " + ex.Message);
        }
    }

    private void Sweep()
    {
        if (Nova.v == null || Nova.v.vehicles == null)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;

        // positions des joueurs en jeu (une seule fois par passage)
        List<Vector3> playerPositions = new List<Vector3>();
        try
        {
            foreach (Player p in Nova.server.GetAllInGamePlayers())
            {
                if (p != null && p.setup != null)
                {
                    try { playerPositions.Add(p.setup.transform.position); } catch { }
                }
            }
        }
        catch
        {
        }

        float radiusSq = config.playerRadiusMeters * config.playerRadiusMeters;
        // copie défensive : TryReplaceCarWithFake peut toucher la liste
        List<LifeVehicle> snapshot = new List<LifeVehicle>(Nova.v.vehicles);
        foreach (LifeVehicle lv in snapshot)
        {
            if (lv == null || lv.isStowed || lv.fake != null || lv.instance == null)
            {
                tracks.Remove(lv != null ? lv.vehicleId : -1);
                continue;
            }

            Vector3 pos;
            try { pos = lv.instance.transform.position; } catch { continue; }

            Track t;
            if (!tracks.TryGetValue(lv.vehicleId, out t))
            {
                t = new Track { lastPos = pos, stillSince = now };
                tracks[lv.vehicleId] = t;
                continue;
            }

            if (Vector3.Distance(pos, t.lastPos) > 2f)
            {
                t.lastPos = pos;
                t.stillSince = now;
                continue;
            }

            if (now - t.stillSince < config.ghostAfterMinutes * 60f)
            {
                continue;
            }

            // un joueur est proche : on ne fait rien (occupant ou spectateur)
            bool near = false;
            for (int i = 0; i < playerPositions.Count; i++)
            {
                if ((playerPositions[i] - pos).sqrMagnitude < radiusSq)
                {
                    near = true;
                    break;
                }
            }
            if (near)
            {
                continue;
            }

            bool ok = false;
            try
            {
                ok = Nova.v.TryReplaceCarWithFake(lv.instance, false, false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[TKGHOST] Erreur conversion vehicleId=" + lv.vehicleId + " : " + ex.Message);
            }
            if (ok)
            {
                TKGhost.ghostedSinceBoot++;
                tracks.Remove(lv.vehicleId);
                if (config.logChanges)
                {
                    Debug.Log("[TKGHOST] Véhicule " + lv.vehicleId + " (" + (lv.plate ?? "?")
                        + ") passé en fantôme après " + config.ghostAfterMinutes + " min d'inactivité");
                }
            }
            else
            {
                // refusé par le jeu (capot ouvert, remorquage...) : on retentera
                t.stillSince = now;
            }
        }
    }
}

[Serializable]
public class TKGhostConfig
{
    public bool enabled = true;
    // Minutes d'immobilité (< 2 m) avant conversion en fantôme
    public int ghostAfterMinutes = 10;
    // Aucune conversion si un joueur est à moins de N mètres
    public float playerRadiusMeters = 50f;
    // Période de passage
    public int checkIntervalSeconds = 30;
    public bool logChanges = true;

    public static string ToJson(TKGhostConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"ghostAfterMinutes\": " + c.ghostAfterMinutes + ",");
        sb.AppendLine("  \"playerRadiusMeters\": " + c.playerRadiusMeters.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"checkIntervalSeconds\": " + c.checkIntervalSeconds + ",");
        sb.AppendLine("  \"logChanges\": " + (c.logChanges ? "true" : "false"));
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKGhostConfig FromJson(string json)
    {
        TKGhostConfig c = new TKGhostConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = GetBool(json, "enabled", c.enabled);
        c.ghostAfterMinutes = GetInt(json, "ghostAfterMinutes", c.ghostAfterMinutes);
        c.playerRadiusMeters = GetInt(json, "playerRadiusMeters", (int)c.playerRadiusMeters);
        c.checkIntervalSeconds = GetInt(json, "checkIntervalSeconds", c.checkIntervalSeconds);
        c.logChanges = GetBool(json, "logChanges", c.logChanges);
        if (c.ghostAfterMinutes < 2) c.ghostAfterMinutes = 2;
        if (c.playerRadiusMeters < 20) c.playerRadiusMeters = 20;
        if (c.checkIntervalSeconds < 10) c.checkIntervalSeconds = 10;
        return c;
    }

    private static bool GetBool(string json, string key, bool def)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)", RegexOptions.IgnoreCase);
        return m.Success ? string.Equals(m.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase) : def;
    }

    private static int GetInt(string json, string key, int def)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+)");
        int v;
        return m.Success && int.TryParse(m.Groups["v"].Value, out v) ? v : def;
    }
}
