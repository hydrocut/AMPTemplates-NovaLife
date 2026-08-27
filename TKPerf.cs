using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Life;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKPerf v1.0 — TeamKit.fr
///
/// Optimiseur pour serveur HEADLESS : sur un serveur sans écran ni carte son,
/// Unity simule quand même des composants purement visuels/sonores sur le
/// thread principal (mesuré saturé à 100 %). TKPerf scanne la scène, mesure,
/// et coupe ce qui ne sert à rien côté serveur :
///  - particules (fumées, fontaines, étincelles...) : arrêtées ;
///  - sources audio : désactivées (pas de carte son de toute façon) ;
///  - seuil de sommeil physique relevé : les objets immobiles s'endorment
///    plus vite et sortent de la simulation.
/// Tout est configurable et le scan re-tourne périodiquement (les objets
/// recréés par le jeu sont re-traités). Rapport dans la console AMP.
/// Config : Plugins/TKPerf/config.json (rechargée à chaud toutes les 60 s).
/// </summary>
public class TKPerf : Plugin
{
    private TKPerfConfig config = new TKPerfConfig();
    private string configPathSaved;
    private string lastConfigJson;

    public TKPerf(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        if (!config.enabled)
        {
            Debug.Log("[TKPERF] Plugin TKPerf v1.0 désactivé par config");
            return;
        }
        try
        {
            GameObject go = new GameObject("TKPerf");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TKPerfTicker ticker = go.AddComponent<TKPerfTicker>();
            ticker.plugin = this;
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKPERF] Ticker impossible : " + ex.Message);
        }
        Debug.Log("[TKPERF] Plugin TKPerf v1.0 initialisé (premier scan dans 90 s"
            + ", puis toutes les " + config.scanIntervalMinutes + " min)");
    }

    public void RunScan()
    {
        Stopwatch sw = Stopwatch.StartNew();
        int psTotal = 0, psStopped = 0, auTotal = 0, auDisabled = 0;
        int rbTotal = 0, rbAwake = 0, anTotal = 0, anEnabled = 0;
        try
        {
            // 1) particules : simulation purement visuelle -> stop
            ParticleSystem[] pss = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            psTotal = pss.Length;
            if (config.stopParticles)
            {
                foreach (ParticleSystem ps in pss)
                {
                    try
                    {
                        if (ps != null && (ps.isPlaying || ps.isEmitting))
                        {
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            psStopped++;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
        try
        {
            // 2) audio : pas de carte son sur le serveur -> désactive
            AudioSource[] aus = UnityEngine.Object.FindObjectsOfType<AudioSource>();
            auTotal = aus.Length;
            if (config.disableAudio)
            {
                foreach (AudioSource au in aus)
                {
                    try
                    {
                        if (au != null && au.enabled)
                        {
                            au.enabled = false;
                            auDisabled++;
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
        try
        {
            // 3) physique : endormir plus vite les objets immobiles
            if (config.sleepThreshold > 0f && Mathf.Abs(Physics.sleepThreshold - config.sleepThreshold) > 0.0001f)
            {
                Physics.sleepThreshold = config.sleepThreshold;
                Debug.Log("[TKPERF] Physics.sleepThreshold = " + config.sleepThreshold.ToString("0.###"));
            }
            Rigidbody[] rbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            rbTotal = rbs.Length;
            foreach (Rigidbody rb in rbs)
            {
                try
                {
                    if (rb != null && !rb.IsSleeping())
                    {
                        rbAwake++;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        try
        {
            // 4) animators : compte seulement (rapport), on ne touche pas —
            //    la logique du jeu peut lire leurs états.
            Animator[] ans = UnityEngine.Object.FindObjectsOfType<Animator>();
            anTotal = ans.Length;
            foreach (Animator an in ans)
            {
                try
                {
                    if (an != null && an.enabled)
                    {
                        anEnabled++;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        sw.Stop();
        Debug.Log("[TKPERF] Scan " + sw.ElapsedMilliseconds + " ms — particules " + psTotal
            + (config.stopParticles ? " (" + psStopped + " arrêtées)" : "")
            + " · audio " + auTotal + (config.disableAudio ? " (" + auDisabled + " désactivées)" : "")
            + " · rigidbodies " + rbTotal + " dont " + rbAwake + " éveillés"
            + " · animators " + anEnabled + "/" + anTotal + " actifs");
    }

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
            config = TKPerfConfig.FromJson(txt);
            Debug.Log("[TKPERF] Config rechargée : actif=" + config.enabled
                + " particules=" + config.stopParticles + " audio=" + config.disableAudio
                + " sleep=" + config.sleepThreshold.ToString("0.###")
                + " scan=" + config.scanIntervalMinutes + " min");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKPERF] Rechargement config : " + ex.Message);
        }
    }

    public int ScanIntervalMinutes()
    {
        return config != null ? config.scanIntervalMinutes : 60;
    }

    public bool IsEnabled()
    {
        return config != null && config.enabled;
    }

    private void LoadConfig()
    {
        try
        {
            string dir = Path.Combine(pluginsPath, "TKPerf");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            configPathSaved = Path.Combine(dir, "config.json");
            if (!File.Exists(configPathSaved))
            {
                File.WriteAllText(configPathSaved, TKPerfConfig.ToJson(config));
                Debug.Log("[TKPERF] config.json créé : " + configPathSaved);
            }
            else
            {
                lastConfigJson = File.ReadAllText(configPathSaved);
                config = TKPerfConfig.FromJson(lastConfigJson);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKPERF] Lecture config : " + ex.Message);
        }
    }
}

public class TKPerfTicker : MonoBehaviour
{
    public TKPerf plugin;
    private float nextScan = 90f;    // premier scan 90 s après le boot
    private float reloadAccum;

    private void Update()
    {
        reloadAccum += Time.unscaledDeltaTime;
        if (reloadAccum >= 60f)
        {
            reloadAccum = 0f;
            try { plugin.ReloadConfig(); } catch { }
        }
        if (Time.realtimeSinceStartup >= nextScan)
        {
            nextScan = Time.realtimeSinceStartup + plugin.ScanIntervalMinutes() * 60f;
            if (plugin.IsEnabled())
            {
                try { plugin.RunScan(); } catch (Exception ex) { Debug.LogError("[TKPERF] Scan : " + ex.Message); }
            }
        }
    }
}

public class TKPerfConfig
{
    public bool enabled = true;
    // Arrêter les systèmes de particules (purement visuels — aucun effet
    // gameplay ; les clients affichent leurs propres particules)
    public bool stopParticles = true;
    // Désactiver les AudioSources (le serveur n'a pas de carte son)
    public bool disableAudio = true;
    // Seuil de sommeil physique (Unity = 0.005). Plus haut = les objets
    // immobiles s'endorment plus vite et sortent de la simulation.
    // 0 = ne pas toucher.
    public float sleepThreshold = 0.05f;
    // Période du scan (minutes) — retraite les objets recréés par le jeu
    public int scanIntervalMinutes = 30;

    public static string ToJson(TKPerfConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"stopParticles\": " + (c.stopParticles ? "true" : "false") + ",");
        sb.AppendLine("  \"disableAudio\": " + (c.disableAudio ? "true" : "false") + ",");
        sb.AppendLine("  \"sleepThreshold\": " + c.sleepThreshold.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"scanIntervalMinutes\": " + c.scanIntervalMinutes);
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKPerfConfig FromJson(string json)
    {
        TKPerfConfig c = new TKPerfConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = GetBool(json, "enabled", c.enabled);
        c.stopParticles = GetBool(json, "stopParticles", c.stopParticles);
        c.disableAudio = GetBool(json, "disableAudio", c.disableAudio);
        c.sleepThreshold = GetFloat(json, "sleepThreshold", c.sleepThreshold);
        if (c.sleepThreshold < 0f) c.sleepThreshold = 0f;
        if (c.sleepThreshold > 0.5f) c.sleepThreshold = 0.5f;
        c.scanIntervalMinutes = (int)GetFloat(json, "scanIntervalMinutes", c.scanIntervalMinutes);
        if (c.scanIntervalMinutes < 5) c.scanIntervalMinutes = 5;
        return c;
    }

    private static bool GetBool(string json, string key, bool def)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value.ToLowerInvariant() == "true" : def;
    }

    private static float GetFloat(string json, string key, float def)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?[0-9.]+)");
        float v;
        if (m.Success && float.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out v))
        {
            return v;
        }
        return def;
    }
}
