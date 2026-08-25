using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using Life.Network;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKDynamicFps v1.0 — TeamKit.fr
///
/// Framerate serveur adaptatif pour Nova-Life : réduit la consommation CPU
/// quand le serveur est vide ou chargé, sans toucher au confort des joueurs.
///
/// Logique (évaluée toutes les intervalSeconds, défaut 10 s) :
///  - 0 joueur           -> idleFps (défaut 20)
///  - joueurs connectés  -> démarre à minPlayersFps (défaut 30) puis s'adapte
///       CPU du process < cpuLowPercent  -> +stepFps jusqu'à maxFps (60)
///       CPU du process > cpuHighPercent -> -stepFps jusqu'à minPlayersFps
///  - Jamais en dessous de idleFps, jamais au-dessus de maxFps.
///  - Dès qu'une connexion arrive pendant l'idle, remontée immédiate
///    (pas d'attente du prochain cycle).
///
/// Le CPU est mesuré sur le process du jeu et normalisé sur allocatedCores
/// (le nombre de cœurs alloués à l'instance AMP, défaut 3) : la valeur
/// correspond donc au % affiché par AMP.
///
/// Config : Plugins/TKDynamicFps/config.json. Logs : [TKFPS] target=...
/// </summary>
public class TKDynamicFps : Plugin
{
    private TKDynamicFpsConfig config;

    public TKDynamicFps(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        if (!config.enabled)
        {
            Debug.Log("[TKFPS] Plugin TKDynamicFps v1.0 désactivé par config");
            return;
        }
        try
        {
            GameObject go = new GameObject("TKDynamicFps");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TKDynamicFpsTicker ticker = go.AddComponent<TKDynamicFpsTicker>();
            ticker.config = config;
            Debug.Log("[TKFPS] Plugin TKDynamicFps v1.0 initialisé (idle "
                + config.idleFps + " / base " + config.minPlayersFps + " / max " + config.maxFps
                + " FPS, seuils CPU " + config.cpuLowPercent + "-" + config.cpuHighPercent
                + "% sur " + config.allocatedCores + " cœurs)");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFPS] Impossible de démarrer le ticker : " + ex.Message);
        }
    }

    private void LoadConfig()
    {
        try
        {
            string dir = Path.Combine(pluginsPath, "TKDynamicFps");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath))
            {
                config = new TKDynamicFpsConfig();
                File.WriteAllText(configPath, TKDynamicFpsConfig.ToJson(config));
                Debug.Log("[TKFPS] config.json créé : " + configPath);
            }
            else
            {
                config = TKDynamicFpsConfig.FromJson(File.ReadAllText(configPath));
                File.WriteAllText(configPath, TKDynamicFpsConfig.ToJson(config));
                Debug.Log("[TKFPS] config.json chargé");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKFPS] Erreur chargement config, valeurs par défaut : " + ex.Message);
            config = new TKDynamicFpsConfig();
        }
    }
}

public class TKDynamicFpsTicker : MonoBehaviour
{
    public TKDynamicFpsConfig config;

    private float accum;
    private Process process;
    private TimeSpan lastCpuTime;
    private float lastWallTime;
    private bool cpuAvailable = true;
    private int desiredFps = -1;

    private void Start()
    {
        try
        {
            process = Process.GetCurrentProcess();
            lastCpuTime = process.TotalProcessorTime;
            lastWallTime = Time.realtimeSinceStartup;
        }
        catch (Exception ex)
        {
            cpuAvailable = false;
            Debug.LogError("[TKFPS] Mesure CPU indisponible (" + ex.Message + "), repli sur "
                + config.minPlayersFps + " FPS avec joueurs");
        }
    }

    private int PlayerCount()
    {
        try
        {
            return NetworkServer.connections != null ? NetworkServer.connections.Count : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void Update()
    {
        if (config == null)
        {
            return;
        }

        // Réaction immédiate : une connexion arrive pendant l'idle
        if (desiredFps > 0 && desiredFps <= config.idleFps && PlayerCount() > 0)
        {
            Apply(config.minPlayersFps, PlayerCount(), -1, "connexion entrante");
        }

        accum += Time.unscaledDeltaTime;
        if (accum < config.intervalSeconds)
        {
            return;
        }
        accum = 0f;

        int players = PlayerCount();
        double cpu = MeasureCpuPercent();

        int current = desiredFps > 0 ? desiredFps : (Application.targetFrameRate > 0 ? Application.targetFrameRate : config.maxFps);
        int target;

        if (players == 0)
        {
            target = config.idleFps;
        }
        else if (cpu < 0)
        {
            // CPU non mesurable : valeur sûre
            target = config.minPlayersFps;
        }
        else if (cpu > config.cpuHighPercent)
        {
            target = Math.Max(config.minPlayersFps, current - config.stepFps);
        }
        else if (cpu < config.cpuLowPercent)
        {
            target = Math.Min(config.maxFps, current + config.stepFps);
        }
        else
        {
            target = current; // zone neutre : hystérésis
        }

        if (target < config.idleFps)
        {
            target = config.idleFps;
        }

        Apply(target, players, cpu, null);
    }

    private void Apply(int fps, int players, double cpu, string reason)
    {
        bool changed = fps != desiredFps || Application.targetFrameRate != fps;
        desiredFps = fps;
        if (Application.targetFrameRate != fps)
        {
            Application.targetFrameRate = fps;
        }
        if (changed && config.logChanges)
        {
            Debug.Log("[TKFPS] target=" + fps + " FPS (joueurs=" + players
                + (cpu >= 0 ? " cpu=" + cpu.ToString("0") + "%" : "")
                + (reason != null ? " — " + reason : "") + ")");
        }
    }

    // % CPU du process depuis le dernier appel, normalisé sur allocatedCores
    private double MeasureCpuPercent()
    {
        if (!cpuAvailable)
        {
            return -1;
        }
        try
        {
            TimeSpan cpuNow = process.TotalProcessorTime;
            float wallNow = Time.realtimeSinceStartup;
            double cpuDelta = (cpuNow - lastCpuTime).TotalSeconds;
            double wallDelta = wallNow - lastWallTime;
            lastCpuTime = cpuNow;
            lastWallTime = wallNow;
            if (wallDelta <= 0.5)
            {
                return -1;
            }
            int cores = config.allocatedCores > 0 ? config.allocatedCores : 1;
            return 100.0 * cpuDelta / (wallDelta * cores);
        }
        catch
        {
            cpuAvailable = false;
            return -1;
        }
    }
}

[Serializable]
public class TKDynamicFpsConfig
{
    public bool enabled = true;
    // FPS serveur vide (plancher absolu, jamais en dessous)
    public int idleFps = 20;
    // FPS de base avec joueurs (plancher quand le CPU est chargé)
    public int minPlayersFps = 30;
    // FPS max, atteint seulement quand le CPU a de la marge
    public int maxFps = 60;
    // Au-dessus de ce % CPU (normalisé sur allocatedCores) : on descend
    public int cpuHighPercent = 80;
    // En dessous de ce % CPU : on monte
    public int cpuLowPercent = 55;
    // Cœurs alloués à l'instance AMP (pour que le % corresponde à l'affichage AMP)
    public int allocatedCores = 3;
    // Période d'évaluation
    public int intervalSeconds = 10;
    // Pas d'ajustement par cycle
    public int stepFps = 5;
    public bool logChanges = true;

    public static string ToJson(TKDynamicFpsConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"idleFps\": " + c.idleFps + ",");
        sb.AppendLine("  \"minPlayersFps\": " + c.minPlayersFps + ",");
        sb.AppendLine("  \"maxFps\": " + c.maxFps + ",");
        sb.AppendLine("  \"cpuHighPercent\": " + c.cpuHighPercent + ",");
        sb.AppendLine("  \"cpuLowPercent\": " + c.cpuLowPercent + ",");
        sb.AppendLine("  \"allocatedCores\": " + c.allocatedCores + ",");
        sb.AppendLine("  \"intervalSeconds\": " + c.intervalSeconds + ",");
        sb.AppendLine("  \"stepFps\": " + c.stepFps + ",");
        sb.AppendLine("  \"logChanges\": " + (c.logChanges ? "true" : "false"));
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKDynamicFpsConfig FromJson(string json)
    {
        TKDynamicFpsConfig c = new TKDynamicFpsConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = GetBool(json, "enabled", c.enabled);
        c.idleFps = GetInt(json, "idleFps", c.idleFps);
        c.minPlayersFps = GetInt(json, "minPlayersFps", c.minPlayersFps);
        c.maxFps = GetInt(json, "maxFps", c.maxFps);
        c.cpuHighPercent = GetInt(json, "cpuHighPercent", c.cpuHighPercent);
        c.cpuLowPercent = GetInt(json, "cpuLowPercent", c.cpuLowPercent);
        c.allocatedCores = GetInt(json, "allocatedCores", c.allocatedCores);
        c.intervalSeconds = GetInt(json, "intervalSeconds", c.intervalSeconds);
        c.stepFps = GetInt(json, "stepFps", c.stepFps);
        c.logChanges = GetBool(json, "logChanges", c.logChanges);

        // Garde-fous
        if (c.idleFps < 10) c.idleFps = 10;
        if (c.minPlayersFps < c.idleFps) c.minPlayersFps = c.idleFps;
        if (c.maxFps < c.minPlayersFps) c.maxFps = c.minPlayersFps;
        if (c.intervalSeconds < 3) c.intervalSeconds = 3;
        if (c.stepFps < 1) c.stepFps = 1;
        if (c.cpuLowPercent >= c.cpuHighPercent) c.cpuLowPercent = c.cpuHighPercent - 10;
        return c;
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
}
