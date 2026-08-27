using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Threading;
using Life;
using Life.Network;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKAntiCheat v1.8 — TeamKit.fr
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
        public int vehSpeedStreak;
        public int flyStreak;
        public float lastFlyX;
        public float lastFlyZ;
        public float lastY;
        public float lastFlyAlert;
        public float ignoreUntil; // téléport légitime : on ignore la vitesse un instant
    }

    private readonly Dictionary<ulong, Track> tracks = new Dictionary<ulong, Track>();
    private HashSet<string> adminWhitelist = new HashSet<string>();
    private readonly Dictionary<ulong, double> adminLastAlert = new Dictionary<ulong, double>();
    private readonly Dictionary<ulong, double> adminFirstSeen = new Dictionary<ulong, double>();
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
            Debug.Log("[TKAC] Plugin TKAntiCheat v1.12 désactivé par config");
            return;
        }
        BuildAdminWhitelist();
        LoadAdminIps();
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
        Debug.Log("[TKAC] Plugin TKAntiCheat v1.13.2 initialisé (ALERTE seule — argent > "
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
            Nova.server.OnPlayerReceiveItemEvent += delegate (Player p, int itemId, int slotId, int number) { OnItem(p, itemId, number); };
            Nova.server.OnPlayerUseCommandEvent += delegate (Player p, SChatCommand cmd) { OnActivity(p, true); };
            Nova.server.OnPlayerConnectEvent += delegate (Player p) { MarkTeleport(p); QueueVpnCheck(p); };
            Nova.server.OnPlayerSpawnCharacterEvent += delegate (Player p) { MarkTeleport(p); QueueVpnCheck(p); };
            Nova.server.OnPlayerDeathEvent += delegate (Player p) { MarkTeleport(p); };
            hooked = true;
            Debug.Log("[TKAC] Événements branchés (argent, items, connexion, spawn, mort)");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur branchement événements : " + ex.Message);
        }
    }

    // Chat : le jeu appelle le hook plugin OnPlayerText pour chaque message
    public override void OnPlayerText(Player player, string message)
    {
        base.OnPlayerText(player, message);
        OnActivity(player, false);
    }

    // steamId -> timestamps des actions (commandes + chat) sur la fenetre
    private readonly Dictionary<ulong, List<double>> spamWindows = new Dictionary<ulong, List<double>>();
    private readonly Dictionary<ulong, double> spamLastAlert = new Dictionary<ulong, double>();

    private void OnActivity(Player p, bool isCommand)
    {
        if (p == null || config == null || !config.spamEnabled)
        {
            return;
        }
        double now = Time.realtimeSinceStartup;
        ulong id = p.steamId;
        List<double> list;
        if (!spamWindows.TryGetValue(id, out list))
        {
            list = new List<double>();
            spamWindows[id] = list;
        }
        list.Add(now);
        double windowStart = now - config.spamWindowSeconds;
        list.RemoveAll(delegate (double t) { return t < windowStart; });

        if (list.Count <= config.spamThreshold)
        {
            return;
        }

        // au-dela du seuil : alerte (throttlee) + kick
        double last;
        spamLastAlert.TryGetValue(id, out last);
        bool logNow = now - last >= 10;
        if (logNow)
        {
            spamLastAlert[id] = now;
        }
        string pseudo = SafePseudo(p);
        Alert("SPAM", pseudo, id, list.Count + " actions (commandes/chat) en " + config.spamWindowSeconds + "s"
            + (config.spamKick ? " — kick" : ""));

        if (config.spamKick)
        {
            try
            {
                p.Disconnect("Anti-spam : trop de commandes/messages en peu de temps");
            }
            catch
            {
            }
            spamWindows.Remove(id);
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

    private static bool IsAdmin(Player p)
    {
        try { return p != null && p.account != null && p.account.adminLevel > 0; }
        catch { return false; }
    }

    private void OnMoney(Player p, double amount, string reason, bool bank)
    {
        if (p == null || amount <= 0 || IsAdmin(p))
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

    // steamId|itemId -> (fenetreDebut, total, dernierLog)
    private readonly Dictionary<string, double[]> itemWindows = new Dictionary<string, double[]>();

    // Détection "spawn d'items" (mod menus) : grosse quantité d'un coup,
    // ou accumulation rapide du même item.
    private void OnItem(Player p, int itemId, int number)
    {
        if (p == null || number <= 0 || IsAdmin(p))
        {
            return;
        }
        if (number >= config.itemAlertQuantity)
        {
            Alert("ITEMS", SafePseudo(p), p.steamId, "reçoit " + number + "x item " + itemId + " d'un coup");
            return;
        }
        string key = p.steamId + "|" + itemId;
        double now = Time.realtimeSinceStartup;
        double[] w;
        if (!itemWindows.TryGetValue(key, out w) || now - w[0] > 60)
        {
            itemWindows[key] = new double[] { now, number, 0 };
            return;
        }
        w[1] += number;
        if (w[1] >= config.itemWindowTotal && now - w[2] > 60)
        {
            w[2] = now;
            Alert("ITEMS", SafePseudo(p), p.steamId, ((int)w[1]) + "x item " + itemId + " en moins d'une minute");
            w[0] = now;
            w[1] = 0;
        }
    }

    private class AdminRow
    {
        public string SteamId { get; set; }
    }

    private void BuildAdminWhitelist()
    {
        adminWhitelist = new HashSet<string>();
        if (config == null)
        {
            return;
        }
        // Filet de sécurité : si la liste est vide alors qu'on en avait une
        // (config écrasée, champ vidé par erreur...), on restaure la dernière
        // liste connue plutôt que de re-partir de la base — sinon un admin
        // momentanément rétrogradé disparaît de la liste et se fait kicker.
        if (string.IsNullOrEmpty(config.adminWhitelist))
        {
            try
            {
                string bak = Path.Combine(pluginDir, "adminwhitelist.bak");
                if (File.Exists(bak))
                {
                    string saved = File.ReadAllText(bak).Trim();
                    if (saved.Length > 0)
                    {
                        config.adminWhitelist = saved;
                        SaveConfig();
                        Debug.Log("[TKAC] Liste blanche vide — restaurée depuis adminwhitelist.bak ("
                            + saved.Split(',').Length + " comptes)");
                    }
                }
            }
            catch
            {
            }
        }
        // Première init : apprend les admins DÉJÀ présents en base (staff légitime)
        // pour ne pas les alerter, et persiste la liste dans la config.
        if (string.IsNullOrEmpty(config.adminWhitelist))
        {
            try
            {
                string db = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "life.db"));
                if (File.Exists(db))
                {
                    SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(db, SQLite.SQLiteOpenFlags.ReadOnly, false);
                    try
                    {
                        List<string> ids = new List<string>();
                        foreach (AdminRow r in conn.Query<AdminRow>("SELECT SteamId FROM Accounts WHERE AdminLevel > 0"))
                        {
                            if (!string.IsNullOrEmpty(r.SteamId))
                            {
                                ids.Add(r.SteamId);
                            }
                        }
                        config.adminWhitelist = string.Join(",", ids.ToArray());
                        SaveConfig();
                        Debug.Log("[TKAC] Liste blanche admin initialisée depuis la base : " + ids.Count + " admin(s) légitime(s)");
                    }
                    finally
                    {
                        conn.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TKAC] Init liste blanche admin : " + ex.Message);
            }
        }
        foreach (string raw in (config.adminWhitelist ?? "").Split(','))
        {
            string id = raw.Trim();
            if (id.Length > 0)
            {
                adminWhitelist.Add(id);
            }
        }
        if (adminWhitelist.Count > 0)
        {
            try
            {
                File.WriteAllText(Path.Combine(pluginDir, "adminwhitelist.bak"),
                    string.Join(",", new List<string>(adminWhitelist).ToArray()));
            }
            catch
            {
            }
        }
    }

    // Relecture à chaud de la config (appelée périodiquement par le ticker)
    private string lastConfigJson;
    public void ReloadConfig()
    {
        try
        {
            string configPath = Path.Combine(pluginDir, "config.json");
            if (!File.Exists(configPath))
            {
                return;
            }
            string json = File.ReadAllText(configPath);
            if (json == lastConfigJson)
            {
                return; // rien n'a changé
            }
            lastConfigJson = json;
            config = TKAntiCheatConfig.FromJson(json);
            BuildAdminWhitelist();
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur relecture config : " + ex.Message);
        }
    }

    private void SaveConfig()
    {
        try
        {
            File.WriteAllText(Path.Combine(pluginDir, "config.json"), TKAntiCheatConfig.ToJson(config));
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur écriture config : " + ex.Message);
        }
    }

    // Détection d'auto-attribution d'admin (mod menu « self-admin »).
    // Appelée par le ticker sur le thread principal.
    public void CheckAdmins(float now)
    {
        if (!config.adminProtection || Nova.server == null)
        {
            return;
        }
        foreach (Player p in Nova.server.GetAllPlayers())
        {
            if (p == null || p.account == null)
            {
                continue;
            }
            int level;
            try { level = p.account.adminLevel; } catch { continue; }
            if (level <= 0)
            {
                continue;
            }
            string steamId = p.steamId.ToString();
            if (adminWhitelist.Contains(steamId))
            {
                adminFirstSeen.Remove(p.steamId);
                continue; // admin légitime déclaré
            }

            // Grâce : une promotion via le panel ajoute le joueur à la liste
            // blanche, relue sous ~20 s — on attend avant de sanctionner,
            // sinon on kick l'admin fraîchement promu (course de vitesse).
            double firstSeen;
            if (!adminFirstSeen.TryGetValue(p.steamId, out firstSeen))
            {
                adminFirstSeen[p.steamId] = now;
                continue;
            }
            if (now - firstSeen < config.adminGraceSeconds)
            {
                continue;
            }

            // admin non autorisé : alerte throttlée
            double last;
            adminLastAlert.TryGetValue(p.steamId, out last);
            if (now - last >= 15)
            {
                adminLastAlert[p.steamId] = now;
                Alert("ADMIN", SafePseudo(p), p.steamId,
                    "possède le niveau admin " + level + " sans être en liste blanche"
                    + (config.adminAutoReset ? " — droits retirés" : "")
                    + (config.adminKick ? " — kick" : ""));
            }

            if (config.adminAutoReset)
            {
                try
                {
                    p.account.adminLevel = 0;
                    p.account.adminPin = "";
                    Life.DB.LifeDB.SaveAccount(p.account);
                    try { p.Notify("Sécurité", "Vos droits admin non autorisés ont été retirés."); } catch { }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TKAC] Erreur retrait admin : " + ex.Message);
                }
            }

            if (config.adminKick)
            {
                try
                {
                    p.Disconnect("Sécurité : niveau admin non autorisé détecté");
                }
                catch
                {
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Anti-usurpation de SteamID (v1.6). Nova-Life ne valide PAS les tickets
    // Steam : le client déclare son SteamID librement. On ne peut pas réparer
    // l'authentification, mais on rend le spoof inutilisable :
    //  - deux joueurs en ligne avec le MÊME SteamID = usurpation certaine
    //  - un compte admin qui se connecte depuis une IP jamais vue = suspect
    // ------------------------------------------------------------------
    private readonly Dictionary<ulong, double> spoofLastAlert = new Dictionary<ulong, double>();
    private readonly Dictionary<ulong, double> ipLastAlert = new Dictionary<ulong, double>();
    private readonly Dictionary<string, HashSet<string>> adminIps = new Dictionary<string, HashSet<string>>();

    private string AdminIpsPath()
    {
        return Path.Combine(pluginDir, "adminips.txt");
    }

    private void LoadAdminIps()
    {
        try
        {
            adminIps.Clear();
            if (!File.Exists(AdminIpsPath()))
            {
                return;
            }
            foreach (string raw in File.ReadAllLines(AdminIpsPath()))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }
                string[] parts = line.Split(';');
                if (parts.Length < 2)
                {
                    continue;
                }
                HashSet<string> ips = new HashSet<string>();
                foreach (string ip in parts[1].Split(','))
                {
                    string t = ip.Trim();
                    if (t.Length > 0)
                    {
                        ips.Add(t);
                    }
                }
                adminIps[parts[0].Trim()] = ips;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKAC] Erreur lecture adminips.txt : " + ex.Message);
        }
    }

    private void SaveAdminIps()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# IP connues par compte admin — steamId;ip1,ip2 (une ligne par compte)");
            sb.AppendLine("# Ajoutez ici la nouvelle IP d'un admin si sa connexion est signalée SPOOF.");
            foreach (KeyValuePair<string, HashSet<string>> kv in adminIps)
            {
                sb.AppendLine(kv.Key + ";" + string.Join(",", new List<string>(kv.Value).ToArray()));
            }
            File.WriteAllText(AdminIpsPath(), sb.ToString());
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // Détecteur VPN/proxy/datacenter (v1.12). À la connexion, on interroge
    // ip-api.com en tâche de fond (pas de blocage du thread principal) et
    // on met le résultat en cache disque. proxy/hosting=true -> alerte +
    // kick optionnel. TrustProxy : la requête ne part QUE pour une IP
    // publique jamais vue, jamais pour les admins ni la liste blanche.
    private readonly Dictionary<string, bool> vpnCache = new Dictionary<string, bool>();
    private readonly HashSet<string> vpnPending = new HashSet<string>();
    private readonly List<ulong> vpnFlagQueue = new List<ulong>();
    private readonly object vpnLock = new object();
    private bool vpnLoaded;

    private string VpnCachePath() { return Path.Combine(pluginDir, "vpncache.tsv"); }

    private void LoadVpnCache()
    {
        if (vpnLoaded) return;
        vpnLoaded = true;
        try
        {
            if (!File.Exists(VpnCachePath())) return;
            foreach (string line in File.ReadAllLines(VpnCachePath()))
            {
                string[] pr = line.Split('\t');
                if (pr.Length >= 2) vpnCache[pr[0]] = pr[1] == "1";
            }
        }
        catch { }
    }

    private bool VpnWhitelisted(string ip)
    {
        if (string.IsNullOrEmpty(config.vpnWhitelist)) return false;
        foreach (string raw in config.vpnWhitelist.Split(','))
        {
            string q = raw.Trim();
            if (q.Length > 0 && ip.StartsWith(q)) return true;
        }
        return false;
    }

    private void QueueVpnCheck(Player p)
    {
        if (config == null || !config.vpnCheck)
        {
            return;
        }
        if (p == null)
        {
            return;
        }
        if (IsAdmin(p))
        {
            Debug.Log("[TKAC] VPN : check ignoré (admin) pour " + p.steamId);
            return;
        }
        string ip = GetIp(p);
        if (string.IsNullOrEmpty(ip))
        {
            Debug.Log("[TKAC] VPN : check reporté (IP indisponible) pour " + p.steamId);
            return;
        }
        if (ip == "127.0.0.1" || ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("172."))
        {
            return;
        }
        if (VpnWhitelisted(ip))
        {
            Debug.Log("[TKAC] VPN : " + ip + " en liste blanche — ignoré");
            return;
        }
        ulong sid = p.steamId;
        bool cached, hasCache;
        lock (vpnLock)
        {
            LoadVpnCache();
            hasCache = vpnCache.TryGetValue(ip, out cached);
            if (!hasCache)
            {
                if (vpnPending.Contains(ip)) return; // déjà en cours
                vpnPending.Add(ip);
            }
        }
        if (hasCache)
        {
            if (cached)
            {
                Debug.Log("[TKAC] VPN : " + ip + " déjà connue VPN (cache) — flag " + sid);
                lock (vpnLock) { vpnFlagQueue.Add(sid); }
            }
            return;
        }
        Debug.Log("[TKAC] VPN : verification de " + ip + " (joueur " + sid + ")");
        Thread t = new Thread(delegate ()
        {
            bool isVpn = QueryVpn(ip);
            lock (vpnLock)
            {
                vpnCache[ip] = isVpn;
                vpnPending.Remove(ip);
                try { File.AppendAllText(VpnCachePath(), ip + "\t" + (isVpn ? "1" : "0") + "\n"); } catch { }
            }
            if (isVpn)
            {
                lock (vpnLock) { vpnFlagQueue.Add(sid); }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // Interroge ip-api.com (gratuit, 45 req/min, sans clé). Renvoie true si
    // l'IP est un proxy/VPN (ou de l'hébergement si vpnBlockHosting).
    private bool QueryVpn(string ip)
    {
        try
        {
            string url = "http://ip-api.com/json/" + ip + "?fields=proxy,hosting,isp,status";
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null; // Mono/Unity : évite l'auto-détection de proxy qui fait échouer/bloquer la requête
            req.Timeout = 6000;
            req.UserAgent = "TKAntiCheat";
            using (WebResponse resp = req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
            {
                string body = sr.ReadToEnd();
                bool proxy = Regex.IsMatch(body, "\"proxy\"\\s*:\\s*true");
                bool hosting = Regex.IsMatch(body, "\"hosting\"\\s*:\\s*true");
                string isp = "";
                Match mi = Regex.Match(body, "\"isp\"\\s*:\\s*\"(?<v>[^\"]*)\"");
                if (mi.Success) isp = mi.Groups["v"].Value;
                // cloud gaming légitime (GeForce NOW...) : toléré même si hosting
                foreach (string raw in (config.vpnIspAllow ?? "").Split(','))
                {
                    string q = raw.Trim();
                    if (q.Length > 1 && isp.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Debug.Log("[TKAC] VPN : " + ip + " -> cloud gaming toléré (" + isp + ")");
                        return false;
                    }
                }
                Debug.Log("[TKAC] VPN : " + ip + " -> proxy=" + proxy + " hosting=" + hosting + " isp=" + isp);
                return proxy || (config.vpnBlockHosting && hosting);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TKAC] VPN : requête échouée pour " + ip + " (" + ex.Message + ") — non bloqué");
            return false; // en cas d'échec API : on ne bloque jamais
        }
    }

    // Draine la file des IP VPN detectees (appele par le ticker, thread
    // principal). Si le joueur n'est pas encore visible (chargement FastDL
    // peut durer 60 s), on RE-ESSAIE a chaque tick pendant 3 minutes :
    // le kick part des qu'il apparait en jeu.
    private readonly Dictionary<ulong, float> vpnFlagRetry = new Dictionary<ulong, float>();

    public void ProcessVpnFlags()
    {
        float now = Time.realtimeSinceStartup;
        lock (vpnLock)
        {
            foreach (ulong sid in vpnFlagQueue)
            {
                if (!vpnFlagRetry.ContainsKey(sid))
                {
                    vpnFlagRetry[sid] = now + 180f; // deadline de re-essai
                }
            }
            vpnFlagQueue.Clear();
        }
        if (vpnFlagRetry.Count == 0) return;
        List<ulong> done = new List<ulong>();
        foreach (KeyValuePair<ulong, float> kv in vpnFlagRetry)
        {
            if (now > kv.Value)
            {
                Debug.Log("[TKAC] VPN : abandon du flag " + kv.Key + " (3 min sans apparaitre en jeu)");
                done.Add(kv.Key);
                continue;
            }
            bool handled = false;
            try { handled = FlagVpn(kv.Key, GetIpBySid(kv.Key)); } catch { handled = true; }
            if (handled)
            {
                done.Add(kv.Key);
            }
        }
        foreach (ulong sid in done)
        {
            vpnFlagRetry.Remove(sid);
        }
    }

    private string GetIpBySid(ulong sid)
    {
        try
        {
            foreach (Player pl in Nova.server.GetAllPlayers())
            {
                if (pl != null && pl.steamId == sid) return GetIp(pl);
            }
        }
        catch { }
        return "?";
    }

    private bool FlagVpn(ulong sid, string ip)
    {
        Player p = null;
        try
        {
            foreach (Player pl in Nova.server.GetAllPlayers())
            {
                if (pl != null && pl.steamId == sid) { p = pl; break; }
            }
        }
        catch { }
        if (p == null)
        {
            return false; // pas encore en jeu : on re-essaiera au tick suivant
        }
        if (IsAdmin(p))
        {
            Debug.Log("[TKAC] VPN : flag abandonné (admin) pour " + sid);
            return true;
        }
        bool kick = config.vpnKick;
        Alert("VPN", SafePseudo(p), sid, "connecté via VPN/proxy/datacenter (" + ip + ")"
            + (kick ? " — kick" : " — surveiller"));
        if (kick)
        {
            try { p.Disconnect("VPN/proxy non autorisé — connecte-toi avec ta vraie connexion"); } catch { }
        }
        return true;
    }

    private string GetIp(Player p)
    {
        try
        {
            NetworkConnectionToClient toClient = p.conn as NetworkConnectionToClient;
            string a = toClient != null ? toClient.address : null;
            if (string.IsNullOrEmpty(a))
            {
                return null;
            }
            if (a.StartsWith("::ffff:"))
            {
                a = a.Substring(7);
            }
            int colon = a.LastIndexOf(':');
            if (colon > 0 && a.IndexOf('.') > 0 && colon > a.IndexOf('.'))
            {
                a = a.Substring(0, colon);
            }
            return a;
        }
        catch
        {
            return null;
        }
    }

    // Appelé par le ticker sur le thread principal
    public void CheckSpoof(float now)
    {
        if (Nova.server == null || (!config.spoofCheck && !config.adminIpGuard))
        {
            return;
        }
        List<Player> players;
        try { players = Nova.server.GetAllPlayers(); } catch { return; }

        if (config.spoofCheck)
        {
            Dictionary<ulong, List<Player>> byId = new Dictionary<ulong, List<Player>>();
            foreach (Player p in players)
            {
                if (p == null || p.steamId == 0)
                {
                    continue;
                }
                List<Player> l;
                if (!byId.TryGetValue(p.steamId, out l))
                {
                    l = new List<Player>();
                    byId[p.steamId] = l;
                }
                l.Add(p);
            }
            foreach (KeyValuePair<ulong, List<Player>> kv in byId)
            {
                if (kv.Value.Count < 2)
                {
                    continue;
                }
                double last;
                spoofLastAlert.TryGetValue(kv.Key, out last);
                if (now - last >= 15)
                {
                    spoofLastAlert[kv.Key] = now;
                    Alert("SPOOF", SafePseudo(kv.Value[0]), kv.Key,
                        kv.Value.Count + " connexions simultanées avec le même SteamID (usurpation)"
                        + (config.spoofKick ? " — kick" : ""));
                }
                if (config.spoofKick)
                {
                    foreach (Player d in kv.Value)
                    {
                        try { d.Disconnect("Sécurité : ce SteamID est déjà en ligne (usurpation détectée)"); } catch { }
                    }
                }
            }
        }

        if (config.adminIpGuard)
        {
            bool dirty = false;
            foreach (Player p in players)
            {
                if (p == null || p.steamId == 0)
                {
                    continue;
                }
                string sid = p.steamId.ToString();
                bool guard = adminWhitelist.Contains(sid);
                if (!guard)
                {
                    try { guard = p.account != null && p.account.adminLevel > 0; } catch { }
                }
                if (!guard)
                {
                    continue;
                }
                string ip = GetIp(p);
                if (string.IsNullOrEmpty(ip))
                {
                    continue;
                }
                HashSet<string> known;
                if (!adminIps.TryGetValue(sid, out known))
                {
                    known = new HashSet<string>();
                    adminIps[sid] = known;
                }
                if (known.Count == 0)
                {
                    known.Add(ip); // première IP vue = référence (apprentissage)
                    dirty = true;
                    continue;
                }
                if (known.Contains(ip))
                {
                    continue;
                }
                double last;
                ipLastAlert.TryGetValue(p.steamId, out last);
                if (now - last >= 30)
                {
                    ipLastAlert[p.steamId] = now;
                    Alert("SPOOF", SafePseudo(p), p.steamId,
                        "compte admin connecté depuis une IP inconnue (" + ip + ")"
                        + (config.adminIpKick ? " — kick" : " — si légitime, ajoutez l'IP dans TKAntiCheat/adminips.txt"));
                }
                if (config.adminIpKick)
                {
                    try { p.Disconnect("Sécurité : IP non reconnue pour ce compte admin"); } catch { }
                }
            }
            if (dirty)
            {
                SaveAdminIps();
            }
        }
    }

    // Appelé par le ticker sur le thread principal
    private static bool IsBadVec(Vector3 v)
    {
        return float.IsNaN(v.x) || float.IsInfinity(v.x)
            || float.IsNaN(v.y) || float.IsInfinity(v.y)
            || float.IsNaN(v.z) || float.IsInfinity(v.z);
    }

    private readonly Dictionary<ulong, float> badPosLastAlert = new Dictionary<ulong, float>();
    private long badPosFixed;

    private void HandleBadPosition(Player p, Track t, float now)
    {
        bool haveSafe = t.hasPos && !IsBadVec(t.lastPos);
        if (haveSafe)
        {
            try
            {
                p.setup.transform.position = t.lastPos;
                Rigidbody rb = p.setup.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch
            {
            }
        }
        else
        {
            // pas de position de repli : on coupe pour arrêter la propagation
            try { p.Disconnect("Position corrompue (protection anti-crash serveur)"); } catch { }
        }
        badPosFixed++;
        float last;
        badPosLastAlert.TryGetValue(p.steamId, out last);
        if (now - last >= 15f)
        {
            badPosLastAlert[p.steamId] = now;
            Alert("CRASH-POS", SafePseudo(p), p.steamId,
                haveSafe ? "position NaN/Infinity corrigée — tentative d'écran noir neutralisée"
                         : "position NaN sans repli — joueur déconnecté");
        }
    }

    // Anti-fly : le véhicule occupé plane-t-il au-dessus de tout support ?
    // Raycast vers le bas (la carte et ses colliders sont chargés côté
    // serveur). En chute libre la hauteur diminue vite -> on ne compte que
    // les relevés où l'altitude est stable ou monte (deltaY > -3 m/s),
    // signature d'un fly hack et pas d'un saut ou d'une chute.
    private void CheckFly(Player p, Track t, Vector3 pos, float dt, float now)
    {
        if (config == null || !config.flyCheck)
        {
            return;
        }
        float deltaY = (pos.y - t.lastY) / (dt > 0.01f ? dt : 1f);
        t.lastY = pos.y;
        // intérieurs spéciaux (grottes, instances) : souvent autour de y=0,
        // et le sol/plafond y piègent les raycasts -> on ne juge pas.
        if (pos.y < 1f)
        {
            t.flyStreak = 0;
            return;
        }
        bool airborne;
        RaycastHit hit;
        if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out hit, config.flyHeight + 1.5f))
        {
            airborne = false; // un support existe sous le véhicule
        }
        else
        {
            airborne = true; // rien sous le véhicule sur flyHeight mètres
        }
        // grotte/tunnel/intérieur : un PLAFOND au-dessus = pas un fly hack
        // (un vrai fly vole à ciel ouvert). Le raycast down peut rater le
        // sol d'une grotte (démarré dans la géométrie), pas celui-ci.
        if (airborne && Physics.Raycast(pos, Vector3.up, 80f))
        {
            airborne = false;
        }
        // un vrai fly hack SE DÉPLACE dans les airs ; un véhicule garé sur un
        // pont/toit ou glitché en hauteur est immobile -> jamais signalé.
        float horiz = new Vector2(pos.x - t.lastFlyX, pos.z - t.lastFlyZ).magnitude;
        t.lastFlyX = pos.x;
        t.lastFlyZ = pos.z;
        bool moving = horiz > 3f; // > ~3 m/s au sol horizontal
        if (airborne && moving && deltaY > -3f)
        {
            t.flyStreak++;
            if (t.flyStreak >= config.flySeconds && now - t.lastFlyAlert >= 30f)
            {
                t.lastFlyAlert = now;
                t.flyStreak = 0;
                Alert("VOL-VHC", SafePseudo(p), p.steamId,
                    "véhicule suspendu à plus de " + config.flyHeight.ToString("0")
                    + " m au-dessus de tout support depuis " + config.flySeconds
                    + " s (alt. " + pos.y.ToString("0") + ") — fly hack probable");
            }
        }
        else
        {
            t.flyStreak = 0;
        }
    }

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
            // Garde anti-crash « écran noir » (pour TOUS, admins compris) :
            // une position NaN/Infinity casse les matrices de collider de
            // tout le monde. On la corrige avant tout le reste.
            if (IsBadVec(pos))
            {
                HandleBadPosition(p, t, now);
                continue;
            }
            if (IsAdmin(p))
            {
                t.lastPos = pos;
                t.lastTime = now;
                t.hasPos = true;
                t.lastY = pos.y;
                continue; // admins exclus des heuristiques vitesse/téléport/fly
            }
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

            bool inVehicle = false;
            try { inVehicle = p.GetVehicle() != null; } catch { }
            if (now < t.ignoreUntil)
            {
                t.overSpeedStreak = 0;
                t.vehSpeedStreak = 0;
                continue;
            }
            if (inVehicle)
            {
                // Speed boost véhicule (menus MelonLoader : moteur trafiqué) :
                // seuil dédié, persistance 3 relevés pour absorber lag/chutes.
                t.overSpeedStreak = 0;
                float vSpeed = dist / dt;
                if (vSpeed > config.maxVehicleSpeed)
                {
                    t.vehSpeedStreak++;
                    if (t.vehSpeedStreak >= 3)
                    {
                        Alert("VITESSE-VHC", SafePseudo(p), p.steamId,
                            (vSpeed * 3.6f).ToString("0") + " km/h soutenus en véhicule (seuil "
                            + (config.maxVehicleSpeed * 3.6f).ToString("0") + " km/h — speed boost probable)");
                        t.vehSpeedStreak = 0;
                    }
                }
                else
                {
                    t.vehSpeedStreak = 0;
                }
                CheckFly(p, t, pos, dt, now);
                continue;
            }
            t.vehSpeedStreak = 0;

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

    // ------------------------------------------------------------------
    // MenuShield (v1.8) : remplace le handler Mirror des CommandMessage par
    // un garde qui reproduit le contrôle d'autorité de Mirror. Différence :
    // Mirror se contente d'un warning anonyme ; ici on retrouve la connexion
    // fautive -> le joueur -> alerte panel avec pseudo/SteamID + nom de la
    // commande, et kick optionnel après N violations. Les ordres légitimes
    // sont transmis tels quels au handler d'origine (aucun impact gameplay ;
    // si la réflexion échoue, le bouclier se désactive tout seul).
    private static Action<NetworkConnectionToClient, CommandMessage, int> forwardCommand;
    private static Func<ushort, bool> cmdRequiresAuth;
    private static Func<ushort, Mirror.RemoteCalls.RemoteCallDelegate> cmdGetDelegate;
    private bool menuShieldInstalled;
    private readonly Dictionary<ulong, int> menuViolations = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, float> menuLastAlert = new Dictionary<ulong, float>();

    public void TryInstallMenuShield()
    {
        if (menuShieldInstalled || config == null || !config.menuShield)
        {
            return;
        }
        if (!NetworkServer.active)
        {
            return; // le serveur réseau n'est pas encore prêt, on réessaiera
        }
        try
        {
            System.Reflection.MethodInfo mi = typeof(NetworkServer).GetMethod(
                "OnCommandMessage",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi == null)
            {
                Debug.LogWarning("[TKAC] MenuShield : OnCommandMessage introuvable — bouclier désactivé");
                menuShieldInstalled = true;
                return;
            }
            forwardCommand = (Action<NetworkConnectionToClient, CommandMessage, int>)Delegate.CreateDelegate(
                typeof(Action<NetworkConnectionToClient, CommandMessage, int>), mi);
            Type rpc = typeof(Mirror.RemoteCalls.RemoteProcedureCalls);
            System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.MethodInfo miAuth = rpc.GetMethod("CommandRequiresAuthority", any);
            if (miAuth == null)
            {
                Debug.LogWarning("[TKAC] MenuShield : CommandRequiresAuthority introuvable — bouclier désactivé");
                menuShieldInstalled = true;
                return;
            }
            cmdRequiresAuth = (Func<ushort, bool>)Delegate.CreateDelegate(typeof(Func<ushort, bool>), miAuth);
            System.Reflection.MethodInfo miGet = rpc.GetMethod("GetDelegate", any);
            if (miGet != null)
            {
                try
                {
                    cmdGetDelegate = (Func<ushort, Mirror.RemoteCalls.RemoteCallDelegate>)Delegate.CreateDelegate(
                        typeof(Func<ushort, Mirror.RemoteCalls.RemoteCallDelegate>), miGet);
                }
                catch
                {
                }
            }
            NetworkServer.ReplaceHandler<CommandMessage>(OnCommandGuard, true);
            menuShieldInstalled = true;
            Debug.Log("[TKAC] MenuShield + CrashGuard actifs — commandes spoofées et payloads NaN jetés, ordres sans autorité attribués");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TKAC] MenuShield non installé (" + ex.Message + ") — comportement Mirror par défaut");
            menuShieldInstalled = true;
        }
    }

    private void OnCommandGuard(NetworkConnectionToClient conn, CommandMessage msg)
    {
        try
        {
            if (config != null && config.menuShield && conn != null)
            {
                // 1) hash de commande inconnu du serveur = commande spoofée
                //    (mod-menu / crasher). C'est le « Found no receiver for
                //    incoming Command » qui inonde la console. On la JETTE.
                bool known = false;
                try { known = cmdGetDelegate != null && cmdGetDelegate(msg.functionHash) != null; }
                catch { known = false; }
                if (!known && config.crashGuard)
                {
                    HandleUnknownCmd(conn, msg);
                    return; // jetée sans alerte : souvent un plugin désynchronisé
                }
                // 2) payload bourré de NaN / Infinity = tentative de crash
                //    « écran noir » (casse les matrices de collider). Jetée.
                if (config.crashGuard && PayloadHasNaNBurst(msg.payload))
                {
                    HandleCrash(conn, msg, "valeurs NaN/Infinity (crash écran noir)");
                    return;
                }
                // 3) ordre sur un objet non possédé = mod-menu « classique »
                //    (hors ordres de synchro à haute fréquence, cf. ignore).
                if (cmdRequiresAuth != null && cmdRequiresAuth(msg.functionHash))
                {
                    NetworkIdentity target;
                    if (NetworkServer.spawned.TryGetValue(msg.netId, out target)
                        && target != null && target.connectionToClient != conn)
                    {
                        HandleMenuViolation(conn, msg, target);
                        return; // ordre illégitime : non transmis
                    }
                }
            }
        }
        catch
        {
        }
        try
        {
            WatchSensitive(conn, msg);
        }
        catch
        {
        }
        try
        {
            if (forwardCommand != null)
            {
                forwardCommand(conn, msg, 0);
            }
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // SensibleWatch : les menus MelonLoader appellent des commandes du jeu
    // (give/spawn/tp/...) sur leur PROPRE joueur -> aucune violation
    // d'autorité, invisible pour MenuShield. On classe chaque hash une
    // seule fois (nom résolu -> matche-t-il un motif sensible ?) puis
    // l'invocation par un non-admin déclenche une alerte attribuée.
    // ALERTE SEULE : la commande est transmise normalement (si le jeu la
    // refuse côté serveur, tant mieux ; si elle passe, l'admin est prévenu).
    private readonly Dictionary<ushort, bool> sensibleCache = new Dictionary<ushort, bool>();
    private readonly Dictionary<ulong, float> sensibleLastAlert = new Dictionary<ulong, float>();
    private string sensiblePatternsCached;
    private string[] sensiblePatterns = new string[0];

    private void WatchSensitive(NetworkConnectionToClient conn, CommandMessage msg)
    {
        if (config == null || !config.sensibleWatch || conn == null)
        {
            return;
        }
        if (!string.Equals(sensiblePatternsCached, config.sensibleWatchPatterns, StringComparison.Ordinal))
        {
            sensiblePatternsCached = config.sensibleWatchPatterns;
            List<string> pats = new List<string>();
            foreach (string raw in (config.sensibleWatchPatterns ?? "").Split(','))
            {
                string q = raw.Trim();
                if (q.Length > 1)
                {
                    pats.Add(q);
                }
            }
            sensiblePatterns = pats.ToArray();
            sensibleCache.Clear();
        }
        bool watched;
        if (!sensibleCache.TryGetValue(msg.functionHash, out watched))
        {
            string name = ResolveCmdName(msg.functionHash);
            watched = false;
            // ne pas alerter sur la conduite/synchro même si un motif matche
            if (name.IndexOf("SendInputs", StringComparison.OrdinalIgnoreCase) < 0)
            {
                for (int i = 0; i < sensiblePatterns.Length; i++)
                {
                    if (name.IndexOf(sensiblePatterns[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        watched = true;
                        break;
                    }
                }
            }
            sensibleCache[msg.functionHash] = watched;
        }
        if (!watched)
        {
            return;
        }
        Player author = FindAuthor(conn);
        if (author == null)
        {
            return;
        }
        int lvl = 0;
        try { lvl = author.account != null ? author.account.adminLevel : 0; } catch { }
        if (lvl > 0)
        {
            return; // admin légitime : rien à signaler
        }
        ulong sid = author.steamId;
        float now = Time.realtimeSinceStartup;
        float last;
        sensibleLastAlert.TryGetValue(sid, out last);
        if (now - last < 30f)
        {
            return;
        }
        sensibleLastAlert[sid] = now;
        Alert("SENSIBLE", SafePseudo(author), sid,
            "commande sensible invoquée sans être admin : " + ResolveCmdName(msg.functionHash)
            + " (signature de menu de triche type MelonLoader)");
    }

    // Détecte une VRAIE injection de crash : au moins 3 flottants NaN/Infinity
    // CONSÉCUTIFS au même pas de 4 octets — c'est la signature d'un Vector3 ou
    // d'un Quaternion entièrement NaN (ce qui casse la matrice de transform).
    // On exige la consécutivité car un scan « au hasard » d'octets de conduite
    // légitime produit des motifs NaN isolés : ne compter que les triplets
    // contigus élimine ces faux positifs (proba d'un triplet légitime ~1e-7).
    private static bool PayloadHasNaNBurst(ArraySegment<byte> seg)
    {
        byte[] a = seg.Array;
        if (a == null || seg.Count < 12 || seg.Count > 8192)
        {
            return false;
        }
        int off = seg.Offset, end = seg.Offset + seg.Count;
        for (int i = off; i + 12 <= end; i++)
        {
            if (IsBadFloat(a, i) && IsBadFloat(a, i + 4) && IsBadFloat(a, i + 8))
            {
                return true; // 3 flottants NaN/Inf d'affilée = transform NaN
            }
        }
        return false;
    }

    private static bool IsBadFloat(byte[] a, int i)
    {
        float f = BitConverter.ToSingle(a, i);
        return float.IsNaN(f) || float.IsInfinity(f);
    }

    private Player FindAuthor(NetworkConnectionToClient conn)
    {
        try
        {
            foreach (Player p in Nova.server.GetAllPlayers())
            {
                if (p != null && ReferenceEquals(p.conn, conn))
                {
                    return p;
                }
            }
        }
        catch
        {
        }
        return null;
    }

    // Fenêtre glissante par joueur : 1 paquet NaN isolé = jeté en silence
    // (les paquets de conduite en produisent parfois par hasard) ; on
    // n'alerte qu'à partir de 3 paquets en 2 min (un vrai crasher en spamme
    // des dizaines), et le kick optionnel ne part qu'à 5.
    private readonly Dictionary<ulong, List<float>> crashTimes = new Dictionary<ulong, List<float>>();
    private readonly Dictionary<ulong, float> crashLastAlert = new Dictionary<ulong, float>();
    private long crashQuietDropped;
    private float crashSummaryLast;

    private string ResolveCmdName(ushort hash)
    {
        try
        {
            Mirror.RemoteCalls.RemoteCallDelegate rd = cmdGetDelegate != null ? cmdGetDelegate(hash) : null;
            if (rd != null && rd.Method != null)
            {
                string comp = rd.Method.DeclaringType != null ? rd.Method.DeclaringType.Name : "?";
                string fn = rd.Method.Name.Replace("InvokeUserCode_", "");
                int cut = fn.IndexOf("__", StringComparison.Ordinal);
                if (cut > 0)
                {
                    fn = fn.Substring(0, cut);
                }
                return comp + "." + fn;
            }
        }
        catch
        {
        }
        return "commande [" + hash + "]";
    }

    private void HandleCrash(NetworkConnectionToClient conn, CommandMessage msg, string why)
    {
        Player author = FindAuthor(conn);
        ulong sid = author != null ? author.steamId : 0;
        float now = Time.realtimeSinceStartup;
        List<float> times;
        if (!crashTimes.TryGetValue(sid, out times))
        {
            times = new List<float>();
            crashTimes[sid] = times;
        }
        times.Add(now);
        times.RemoveAll(delegate (float t) { return now - t > 120f; });
        int n = times.Count;
        crashQuietDropped++;
        // résumé discret : la protection travaille, sans accuser personne
        // (les VICTIMES de la contagion NaN émettent aussi des paquets NaN)
        if (now - crashSummaryLast >= 60f)
        {
            crashSummaryLast = now;
            Debug.Log("[TKAC] CrashGuard : " + crashQuietDropped
                + " paquet(s) NaN jetés depuis le démarrage (" + crashTimes.Count
                + " émetteur(s) — attaquant ET victimes de la contagion mélangés)");
        }
        // Seuls les ATTAQUANTS (flot massif) sont signalés ; les victimes de
        // la contagion (quelques paquets) sont jetées en silence, jamais
        // accusées ni kickées.
        if (n < config.crashAttackerPackets)
        {
            return;
        }
        if (!config.crashGuardAlert && !config.crashGuardKick)
        {
            return;
        }
        string pseudo = author != null ? SafePseudo(author) : ("connexion #" + conn.connectionId);
        if (string.IsNullOrEmpty(pseudo) || pseudo.Trim().Length == 0)
        {
            pseudo = "Joueur " + sid;
        }
        bool kick = config.crashGuardKick && author != null && n >= config.crashAttackerPackets * 3;
        float last;
        crashLastAlert.TryGetValue(sid, out last);
        if (now - last >= 30f || kick)
        {
            crashLastAlert[sid] = now;
            Alert("CRASH", pseudo, sid, "FLOT de paquets NaN (attaquant probable) : " + why
                + " via " + ResolveCmdName(msg.functionHash)
                + " — " + n + " paquet(s) en 2 min" + (kick ? " — kick" : ""));
        }
        if (kick)
        {
            times.Clear();
            try { author.Disconnect("Paquets malformés répétés (crash NaN)"); } catch { }
        }
    }

    // Commandes au hash inconnu : jetées en silence. Constat du 27/08 :
    // 115 500 paquets du MÊME hash [46406] venaient d'un plugin FastDL
    // client désynchronisé, pas d'une attaque -> aucune alerte, un simple
    // compteur par hash loggé au plus 1 fois/min.
    private class UnknownCmdInfo
    {
        public long count;
        public readonly HashSet<ulong> senders = new HashSet<ulong>();
        public bool alerted;
    }
    private readonly Dictionary<ushort, UnknownCmdInfo> unknownCmds = new Dictionary<ushort, UnknownCmdInfo>();
    private float unknownCmdLastLog;

    private void HandleUnknownCmd(NetworkConnectionToClient conn, CommandMessage msg)
    {
        UnknownCmdInfo info;
        if (!unknownCmds.TryGetValue(msg.functionHash, out info))
        {
            info = new UnknownCmdInfo();
            unknownCmds[msg.functionHash] = info;
        }
        info.count++;
        Player author = FindAuthor(conn);
        ulong sid = author != null ? author.steamId : 0;
        if (sid != 0 && info.senders.Count < 16)
        {
            info.senders.Add(sid);
        }
        // Signature d'un MOD CLIENT injecté (MelonLoader) : un hash inconnu
        // envoyé de façon répétée par 1-2 joueurs SEULEMENT. Un plugin
        // client/serveur désynchronisé (ex. FastDL) est au contraire émis
        // par presque tous les joueurs -> jamais d'alerte. On attend 10 min
        // de fonctionnement et 20 occurrences pour trancher sereinement.
        float now = Time.realtimeSinceStartup;
        if (!info.alerted && now > 600f && info.count >= 20
            && info.senders.Count > 0 && info.senders.Count <= 2 && author != null)
        {
            info.alerted = true;
            Alert("MOD-CLIENT", SafePseudo(author), sid,
                "commande client inconnue [" + msg.functionHash + "] envoyée " + info.count
                + " fois par " + info.senders.Count + " joueur(s) seulement — mod client injecté (MelonLoader ?) très probable");
        }
        if (now - unknownCmdLastLog >= 60f)
        {
            unknownCmdLastLog = now;
            StringBuilder sb = new StringBuilder("[TKAC] CrashGuard : commandes inconnues jetées —");
            foreach (KeyValuePair<ushort, UnknownCmdInfo> kv in unknownCmds)
            {
                sb.Append(" [").Append(kv.Key).Append("]x").Append(kv.Value.count)
                  .Append("(").Append(kv.Value.senders.Count).Append(" émetteurs)");
            }
            sb.Append(" — beaucoup d'émetteurs = plugin désynchronisé (FastDL), 1-2 = mod client");
            Debug.Log(sb.ToString());
        }
    }

    private long menuQuietCount;
    private float menuQuietLastLog;

    private void HandleMenuViolation(NetworkConnectionToClient conn, CommandMessage msg, NetworkIdentity target)
    {
        Player author = null;
        try
        {
            foreach (Player p in Nova.server.GetAllPlayers())
            {
                if (p != null && ReferenceEquals(p.conn, conn))
                {
                    author = p;
                    break;
                }
            }
        }
        catch
        {
        }
        string cmdName = ResolveCmdName(msg.functionHash);
        // ordres de synchro connus (désync bénigne) : compteur discret, ni
        // alerte ni sanction — seuls les VRAIS ordres suspects alertent.
        try
        {
            foreach (string pat in (config.menuShieldIgnore ?? "").Split(','))
            {
                string q = pat.Trim();
                if (q.Length > 0 && cmdName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    menuQuietCount++;
                    float nowQ = Time.realtimeSinceStartup;
                    if (nowQ - menuQuietLastLog >= 600f)
                    {
                        menuQuietLastLog = nowQ;
                        Debug.Log("[TKAC] MenuShield : " + menuQuietCount
                            + " ordre(s) de synchro désynchronisés ignorés depuis le démarrage (" + q + ")");
                    }
                    return;
                }
            }
        }
        catch
        {
        }
        ulong sid = author != null ? author.steamId : 0;
        string pseudo = author != null ? SafePseudo(author) : ("connexion #" + conn.connectionId);
        if (string.IsNullOrEmpty(pseudo) || pseudo.Trim().Length == 0)
        {
            pseudo = "Joueur " + sid;
        }
        int n;
        menuViolations.TryGetValue(sid, out n);
        n++;
        menuViolations[sid] = n;
        bool kick = config.menuShieldKick && n >= config.menuShieldThreshold && author != null;
        float now = Time.realtimeSinceStartup;
        float last;
        menuLastAlert.TryGetValue(sid, out last);
        if (n == 1 || kick || now - last >= 30f)
        {
            menuLastAlert[sid] = now;
            Alert("MOD-MENU", pseudo, sid, "ordre réseau sans autorité : " + cmdName
                + " sur « " + (target != null ? target.name : "?") + " » — " + n + " violation(s)"
                + (kick ? " — kick" : ""));
        }
        if (kick)
        {
            menuViolations[sid] = 0;
            try
            {
                author.Disconnect("Ordres réseau non autorisés (menu de triche ?)");
            }
            catch
            {
            }
        }
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
    private float reloadAccum;

    private void Update()
    {
        // relit la config toutes les 20 s pour appliquer à chaud les réglages
        // changés depuis le panel (protection admin, retrait auto, anti-spam)
        reloadAccum += Time.unscaledDeltaTime;
        if (reloadAccum >= 20f)
        {
            reloadAccum = 0f;
            try { plugin.ReloadConfig(); } catch { }
        }

        accum += Time.unscaledDeltaTime;
        if (accum < intervalSeconds)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;
        accum = 0f;
        try
        {
            plugin.TryInstallMenuShield();
            plugin.ProcessVpnFlags();
            plugin.CheckSpeeds(now);
            plugin.CheckAdmins(now);
            plugin.CheckSpoof(now);
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
    // Vitesse max tolérée EN véhicule (m/s). 70 m/s = 252 km/h, au-dessus
    // de tout véhicule légitime du jeu ; détecte les speed boosts injectés.
    public float maxVehicleSpeed = 70f;
    // Anti-fly véhicule : altitude au-dessus de tout support (raycast) qui
    // déclenche, et nombre de relevés consécutifs exigés (1 relevé/s).
    // Les sauts, tremplins et ponts passent largement sous ces seuils.
    // Détecteur VPN/proxy/datacenter (via ip-api.com, gratuit sans clé).
    public bool vpnCheck = true;
    public bool vpnKick = false;              // kick auto si VPN détecté
    public bool vpnBlockHosting = true;       // traiter aussi l'hébergement/datacenter comme VPN
    public string vpnWhitelist = "";          // IP ou préfixes exemptés (virgules)
    // FAI tolérés malgré hosting=true : cloud gaming (les joueurs GeForce
    // NOW & co jouent depuis un datacenter légitime). Sous-chaînes, virgules.
    public string vpnIspAllow = "NVIDIA,GeForce,Shadow,Blade,Boosteroid";
    public bool flyCheck = false;
    public float flyHeight = 25f;
    public int flySeconds = 5;
    // Bond de distance en un relevé = téléport manifeste (m)
    public float teleportDistance = 120f;
    // Grâce après connexion/mort/spawn (s) : on ne juge pas la vitesse
    public float teleportGraceSeconds = 6f;
    // Quantité d'un même item reçue en une fois qui déclenche une alerte
    public int itemAlertQuantity = 50;
    // Total du même item accumulé en < 60 s qui déclenche une alerte
    public int itemWindowTotal = 200;
    // Période de relevé des positions (s)
    public int checkIntervalSeconds = 1;
    // Nb d'alertes gardées en mémoire/fichier
    public int maxAlerts = 200;
    // Protection contre l'auto-attribution d'admin (mod menu « self-admin »)
    public bool adminProtection = true;
    // SteamID des admins LEGITIMES (séparés par des virgules) — les seuls
    // autorisés à avoir un niveau admin. Vide = toute présence d'admin alerte.
    public string adminWhitelist = "";
    // Retirer automatiquement les droits d'un admin non listé (déconseillé
    // tant que la liste blanche n'est pas complète : mets d'abord tes staff)
    public bool adminAutoReset = false;
    // Kick automatique du joueur détecté avec un niveau admin non autorisé
    public bool adminKick = false;
    // Délai de grâce (s) avant de sanctionner un admin hors liste blanche :
    // une promotion légitime via le panel met jusqu'à ~20 s à être relue.
    public int adminGraceSeconds = 35;
    // Anti-usurpation : deux connexions simultanées avec le même SteamID = spoof
    public bool spoofCheck = true;
    public bool spoofKick = true;
    // Garde IP : alerte si un compte admin se connecte depuis une IP jamais vue
    // (apprentissage : la première IP vue devient la référence ; adminips.txt)
    public bool adminIpGuard = false;
    public bool adminIpKick = false;
    // Anti-spam de commandes/chat en jeu
    public bool spamEnabled = true;
    public int spamThreshold = 12;      // actions max sur la fenetre avant sanction
    public int spamWindowSeconds = 5;
    public bool spamKick = true;        // kick auto le spammeur (choix du user)
    // Bouclier commandes réseau (anti mod-menu) : intercepte les Commands
    // Mirror AVANT le jeu et identifie le joueur qui envoie des ordres sur
    // des objets qu'il ne possède pas — la signature d'un menu de triche.
    // L'ordre illégitime est bloqué (Mirror l'aurait refusé aussi), mais on
    // sait désormais QUI, avec le nom de la commande visée.
    public bool menuShield = true;
    public bool menuShieldKick = false;
    public int menuShieldThreshold = 3; // violations avant kick (si kick actif)
    // CrashGuard : nb de paquets NaN en 2 min au-dessus duquel on considère
    // un ATTAQUANT (et non une victime de la contagion). Alerte >= ce seuil,
    // kick (si crashGuardKick) à 3x ce seuil. Défaut haut pour épargner les
    // victimes qui n'émettent que quelques paquets.
    public int crashAttackerPackets = 80;
    // Ordres réseau ignorés par le bouclier (sous-chaînes, séparées par des
    // virgules). CmdSendInputs = synchro volant/pédales : un client désync
    // (véhicule passé fantôme, reconnexion en voiture, passager) en envoie
    // légitimement sur un véhicule qui n'est plus « à lui » — ce n'est pas
    // un mod menu. Comptés à part, jamais alertés ni sanctionnés.
    // Motifs de commandes ignorés par MenuShield (sous-chaîne du nom
    // Composant.Méthode). Les commandes véhicule (Vehicle*, RCC*) suivent
    // un modèle d'autorité propre au jeu et faux-positivent : exclues.
    public string menuShieldIgnore = "CmdSendInputs,Vehicle,RCC";
    // CrashGuard : jette les commandes spoofées (hash inconnu) et les payloads
    // NaN/Infinity qui provoquent les écrans noirs. Actif par défaut.
    public bool crashGuard = true;
    public bool crashGuardKick = false;
    // SensibleWatch : alerte quand un joueur NON admin invoque une commande
    // dont le nom contient un de ces motifs (menus MelonLoader type Hello
    // Kitty / Akayro). Alerte seule, jamais de blocage automatique.
    // Alertes CRASH par joueur : OFF par défaut — les victimes de la
    // contagion NaN émettent aussi des paquets NaN, l'attribution accuse
    // des innocents. Le blocage, lui, est toujours actif (crashGuard).
    public bool crashGuardAlert = false;
    public bool sensibleWatch = true;
    public string sensibleWatchPatterns = "Admin,Give,Spawn,Revive,SetHealth,SetMoney,SetBank,SetJob,Teleport,Weather,SetTime,Announce,ClearInventory";
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
        sb.AppendLine("  \"maxVehicleSpeed\": " + c.maxVehicleSpeed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"vpnCheck\": " + (c.vpnCheck ? "true" : "false") + ",");
        sb.AppendLine("  \"vpnKick\": " + (c.vpnKick ? "true" : "false") + ",");
        sb.AppendLine("  \"vpnBlockHosting\": " + (c.vpnBlockHosting ? "true" : "false") + ",");
        sb.AppendLine("  \"vpnWhitelist\": \"" + (c.vpnWhitelist ?? "").Replace("\\", "").Replace("\"", "") + "\",");
        sb.AppendLine("  \"vpnIspAllow\": \"" + (c.vpnIspAllow ?? "").Replace("\\", "").Replace("\"", "") + "\",");
        sb.AppendLine("  \"flyCheck\": " + (c.flyCheck ? "true" : "false") + ",");
        sb.AppendLine("  \"flyHeight\": " + c.flyHeight.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"flySeconds\": " + c.flySeconds + ",");
        sb.AppendLine("  \"teleportDistance\": " + c.teleportDistance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"teleportGraceSeconds\": " + c.teleportGraceSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ",");
        sb.AppendLine("  \"itemAlertQuantity\": " + c.itemAlertQuantity + ",");
        sb.AppendLine("  \"itemWindowTotal\": " + c.itemWindowTotal + ",");
        sb.AppendLine("  \"checkIntervalSeconds\": " + c.checkIntervalSeconds + ",");
        sb.AppendLine("  \"maxAlerts\": " + c.maxAlerts + ",");
        sb.AppendLine("  \"adminProtection\": " + (c.adminProtection ? "true" : "false") + ",");
        sb.AppendLine("  \"adminWhitelist\": \"" + (c.adminWhitelist ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",");
        sb.AppendLine("  \"adminAutoReset\": " + (c.adminAutoReset ? "true" : "false") + ",");
        sb.AppendLine("  \"adminKick\": " + (c.adminKick ? "true" : "false") + ",");
        sb.AppendLine("  \"adminGraceSeconds\": " + c.adminGraceSeconds + ",");
        sb.AppendLine("  \"spoofCheck\": " + (c.spoofCheck ? "true" : "false") + ",");
        sb.AppendLine("  \"spoofKick\": " + (c.spoofKick ? "true" : "false") + ",");
        sb.AppendLine("  \"adminIpGuard\": " + (c.adminIpGuard ? "true" : "false") + ",");
        sb.AppendLine("  \"adminIpKick\": " + (c.adminIpKick ? "true" : "false") + ",");
        sb.AppendLine("  \"spamEnabled\": " + (c.spamEnabled ? "true" : "false") + ",");
        sb.AppendLine("  \"spamThreshold\": " + c.spamThreshold + ",");
        sb.AppendLine("  \"spamWindowSeconds\": " + c.spamWindowSeconds + ",");
        sb.AppendLine("  \"spamKick\": " + (c.spamKick ? "true" : "false") + ",");
        sb.AppendLine("  \"menuShield\": " + (c.menuShield ? "true" : "false") + ",");
        sb.AppendLine("  \"menuShieldKick\": " + (c.menuShieldKick ? "true" : "false") + ",");
        sb.AppendLine("  \"menuShieldThreshold\": " + c.menuShieldThreshold + ",");
        sb.AppendLine("  \"crashAttackerPackets\": " + c.crashAttackerPackets + ",");
        sb.AppendLine("  \"crashGuard\": " + (c.crashGuard ? "true" : "false") + ",");
        sb.AppendLine("  \"crashGuardKick\": " + (c.crashGuardKick ? "true" : "false") + ",");
        sb.AppendLine("  \"crashGuardAlert\": " + (c.crashGuardAlert ? "true" : "false") + ",");
        sb.AppendLine("  \"sensibleWatch\": " + (c.sensibleWatch ? "true" : "false") + ",");
        sb.AppendLine("  \"sensibleWatchPatterns\": \"" + (c.sensibleWatchPatterns ?? "").Replace("\\", "").Replace("\"", "") + "\",");
        sb.AppendLine("  \"menuShieldIgnore\": \"" + (c.menuShieldIgnore ?? "").Replace("\\", "").Replace("\"", "") + "\",");
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
        c.maxVehicleSpeed = (float)GetDouble(json, "maxVehicleSpeed", c.maxVehicleSpeed);
        c.vpnCheck = GetBool(json, "vpnCheck", c.vpnCheck);
        c.vpnKick = GetBool(json, "vpnKick", c.vpnKick);
        c.vpnBlockHosting = GetBool(json, "vpnBlockHosting", c.vpnBlockHosting);
        Match vwm = Regex.Match(json, @"""vpnWhitelist""\s*:\s*""(?<v>[^""]*)""");
        if (vwm.Success) c.vpnWhitelist = vwm.Groups["v"].Value;
        Match via = Regex.Match(json, @"""vpnIspAllow""\s*:\s*""(?<v>[^""]*)""");
        if (via.Success) c.vpnIspAllow = via.Groups["v"].Value;
        c.flyCheck = GetBool(json, "flyCheck", c.flyCheck);
        c.flyHeight = (float)GetDouble(json, "flyHeight", c.flyHeight);
        if (c.flyHeight < 8f) c.flyHeight = 8f;
        c.flySeconds = (int)GetDouble(json, "flySeconds", c.flySeconds);
        if (c.flySeconds < 3) c.flySeconds = 3;
        c.teleportDistance = (float)GetDouble(json, "teleportDistance", c.teleportDistance);
        c.teleportGraceSeconds = (float)GetDouble(json, "teleportGraceSeconds", c.teleportGraceSeconds);
        c.itemAlertQuantity = (int)GetDouble(json, "itemAlertQuantity", c.itemAlertQuantity);
        c.itemWindowTotal = (int)GetDouble(json, "itemWindowTotal", c.itemWindowTotal);
        c.checkIntervalSeconds = (int)GetDouble(json, "checkIntervalSeconds", c.checkIntervalSeconds);
        c.maxAlerts = (int)GetDouble(json, "maxAlerts", c.maxAlerts);
        c.adminProtection = GetBool(json, "adminProtection", c.adminProtection);
        Match aw = Regex.Match(json, @"""adminWhitelist""\s*:\s*""(?<v>(?:\\.|[^""])*)""");
        if (aw.Success) c.adminWhitelist = aw.Groups["v"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        c.adminAutoReset = GetBool(json, "adminAutoReset", c.adminAutoReset);
        c.adminKick = GetBool(json, "adminKick", c.adminKick);
        c.adminGraceSeconds = (int)GetDouble(json, "adminGraceSeconds", c.adminGraceSeconds);
        c.spoofCheck = GetBool(json, "spoofCheck", c.spoofCheck);
        c.spoofKick = GetBool(json, "spoofKick", c.spoofKick);
        c.adminIpGuard = GetBool(json, "adminIpGuard", c.adminIpGuard);
        c.adminIpKick = GetBool(json, "adminIpKick", c.adminIpKick);
        c.spamEnabled = GetBool(json, "spamEnabled", c.spamEnabled);
        c.spamThreshold = (int)GetDouble(json, "spamThreshold", c.spamThreshold);
        c.spamWindowSeconds = (int)GetDouble(json, "spamWindowSeconds", c.spamWindowSeconds);
        c.spamKick = GetBool(json, "spamKick", c.spamKick);
        c.menuShield = GetBool(json, "menuShield", c.menuShield);
        c.menuShieldKick = GetBool(json, "menuShieldKick", c.menuShieldKick);
        c.menuShieldThreshold = (int)GetDouble(json, "menuShieldThreshold", c.menuShieldThreshold);
        if (c.menuShieldThreshold < 1) c.menuShieldThreshold = 1;
        c.crashAttackerPackets = (int)GetDouble(json, "crashAttackerPackets", c.crashAttackerPackets);
        if (c.crashAttackerPackets < 20) c.crashAttackerPackets = 20;
        c.crashGuard = GetBool(json, "crashGuard", c.crashGuard);
        c.crashGuardKick = GetBool(json, "crashGuardKick", c.crashGuardKick);
        c.crashGuardAlert = GetBool(json, "crashGuardAlert", c.crashGuardAlert);
        c.sensibleWatch = GetBool(json, "sensibleWatch", c.sensibleWatch);
        Match swm = Regex.Match(json, @"""sensibleWatchPatterns""\s*:\s*""(?<v>[^""]*)""");
        if (swm.Success) c.sensibleWatchPatterns = swm.Groups["v"].Value;
        Match msi = Regex.Match(json, @"""menuShieldIgnore""\s*:\s*""(?<v>[^""]*)""");
        if (msi.Success) c.menuShieldIgnore = msi.Groups["v"].Value;
        if (c.spamThreshold < 5) c.spamThreshold = 5;
        if (c.spamWindowSeconds < 2) c.spamWindowSeconds = 2;
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
