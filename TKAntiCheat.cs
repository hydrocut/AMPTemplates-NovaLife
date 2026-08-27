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
            Debug.Log("[TKAC] Plugin TKAntiCheat v1.10 désactivé par config");
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
        Debug.Log("[TKAC] Plugin TKAntiCheat v1.10 initialisé (ALERTE seule — argent > "
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
            Nova.server.OnPlayerConnectEvent += delegate (Player p) { MarkTeleport(p); };
            Nova.server.OnPlayerSpawnCharacterEvent += delegate (Player p) { MarkTeleport(p); };
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

    // steamId|itemId -> (fenetreDebut, total, dernierLog)
    private readonly Dictionary<string, double[]> itemWindows = new Dictionary<string, double[]>();

    // Détection "spawn d'items" (mod menus) : grosse quantité d'un coup,
    // ou accumulation rapide du même item.
    private void OnItem(Player p, int itemId, int number)
    {
        if (p == null || number <= 0)
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
                    HandleUnknownCmd(msg);
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
        if (!config.crashGuardAlert || n < 3)
        {
            return;
        }
        string pseudo = author != null ? SafePseudo(author) : ("connexion #" + conn.connectionId);
        if (string.IsNullOrEmpty(pseudo) || pseudo.Trim().Length == 0)
        {
            pseudo = "Joueur " + sid;
        }
        bool kick = config.crashGuardKick && author != null && n >= 20;
        float last;
        crashLastAlert.TryGetValue(sid, out last);
        if (now - last >= 30f || kick)
        {
            crashLastAlert[sid] = now;
            Alert("CRASH", pseudo, sid, "émet des paquets NaN (crasher OU victime de la contagion) : " + why
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
    private readonly Dictionary<ushort, long> unknownCmdCounts = new Dictionary<ushort, long>();
    private float unknownCmdLastLog;

    private void HandleUnknownCmd(CommandMessage msg)
    {
        long n;
        unknownCmdCounts.TryGetValue(msg.functionHash, out n);
        unknownCmdCounts[msg.functionHash] = n + 1;
        float now = Time.realtimeSinceStartup;
        if (now - unknownCmdLastLog >= 60f)
        {
            unknownCmdLastLog = now;
            StringBuilder sb = new StringBuilder("[TKAC] CrashGuard : commandes inconnues jetées —");
            foreach (KeyValuePair<ushort, long> kv in unknownCmdCounts)
            {
                sb.Append(" [").Append(kv.Key).Append("]x").Append(kv.Value);
            }
            sb.Append(" (souvent un plugin client/serveur désynchronisé, ex. FastDL)");
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
    // Ordres réseau ignorés par le bouclier (sous-chaînes, séparées par des
    // virgules). CmdSendInputs = synchro volant/pédales : un client désync
    // (véhicule passé fantôme, reconnexion en voiture, passager) en envoie
    // légitimement sur un véhicule qui n'est plus « à lui » — ce n'est pas
    // un mod menu. Comptés à part, jamais alertés ni sanctionnés.
    public string menuShieldIgnore = "CmdSendInputs";
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
