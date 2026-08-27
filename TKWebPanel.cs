using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Life;
using Life.DB;
using Life.InventorySystem;
using Life.Network;
using Life.PermissionSystem;
using Life.VehicleSystem;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKWebPanel v2.0 — TeamKit.fr
///
/// Panel d'administration web embarqué dans le serveur Nova-Life.
/// Le plugin démarre un serveur HTTP (port configurable, défaut 7791) qui
/// sert une interface web + une API JSON connectées en direct au jeu :
///   - Joueurs en ligne temps réel (personnage, argent, banque, vie, position)
///   - Kick / Ban (durée ou permanent, fonctionne aussi hors-ligne) / Unban
///   - Économie : donner / retirer espèces et banque
///   - Message privé, annonce serveur
///   - Soigner, téléporter (coordonnées ou vers un joueur)
///   - Monitoring : FPS réel, CPU, RAM, uptime + IP bannies TKAntiFlood
///
/// Sécurité : mot de passe obligatoire (généré au premier démarrage dans
/// Plugins/TKWebPanel/config.json, header X-Auth). Attention : HTTP simple,
/// utilisez un mot de passe fort.
///
/// Toutes les actions de jeu passent par le thread principal Unity via un
/// dispatcher (les threads HTTP ne touchent jamais l'API du jeu directement).
/// </summary>
public class TKWebPanel : Plugin
{
    public static TKWebPanelConfig config;
    public static string pluginDir;
    public static TKWebPanelDispatcher dispatcher;

    private HttpListener listener;
    private Thread httpThread;

    public TKWebPanel(IGameAPI api) : base(api)
    {
    }

    // ------------------------------------------------------------------
    // Chat : capture, historique fichier, diffusion
    // ------------------------------------------------------------------
    private static readonly object chatLock = new object();
    private static readonly List<string> chatRing = new List<string>(); // entrées JSON prêtes
    private static long chatLastId;
    private static string chatDir;

    public override void OnPlayerText(Player player, string message)
    {
        base.OnPlayerText(player, message);
        try
        {
            string pseudo = player != null ? (player.steamUsername ?? "?") : "?";
            string steamId = player != null ? player.steamId.ToString() : "";
            RecordChat(pseudo, steamId, message);
        }
        catch
        {
        }
    }

    private static void RecordChat(string pseudo, string steamId, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        string time = DateTime.Now.ToString("HH:mm:ss");
        lock (chatLock)
        {
            chatLastId++;
            string json = "{\"id\":" + chatLastId + ",\"time\":" + Json.Str(time)
                + ",\"pseudo\":" + Json.Str(pseudo) + ",\"steamId\":\"" + steamId + "\""
                + ",\"text\":" + Json.Str(text) + "}";
            chatRing.Add(json);
            while (chatRing.Count > 300)
            {
                chatRing.RemoveAt(0);
            }
        }
        try
        {
            if (chatDir != null)
            {
                File.AppendAllText(Path.Combine(chatDir, "chat-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    "[" + time + "] " + pseudo + " (" + steamId + ") : " + text.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------
    // Journal d'activité : PvP, drogue, commandes (v2.2)
    // ------------------------------------------------------------------
    // nom du compte panel qui effectue la requete en cours (approximation
    // suffisante pour un panel a faible trafic)
    private static volatile string panelActor = "owner";

    private static void StaffLog(string detail)
    {
        RecordActivity("STAFF", "[" + panelActor + "]", "", detail);
    }

    private static readonly object actLock = new object();
    private static readonly List<string> actRing = new List<string>();
    private static long actLastId;
    private static string actDir;

    private static void RecordActivity(string kind, string pseudo, string steamId, string detail)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        lock (actLock)
        {
            actLastId++;
            actRing.Add("{\"id\":" + actLastId + ",\"time\":" + Json.Str(time)
                + ",\"kind\":" + Json.Str(kind) + ",\"pseudo\":" + Json.Str(pseudo)
                + ",\"steamId\":\"" + steamId + "\",\"detail\":" + Json.Str(detail) + "}");
            while (actRing.Count > 400)
            {
                actRing.RemoveAt(0);
            }
        }
        try
        {
            if (actDir != null)
            {
                File.AppendAllText(Path.Combine(actDir, "activity-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    "[" + time + "] " + kind + " — " + pseudo + " (" + steamId + ") : "
                    + detail.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static string PseudoOf(Player p)
    {
        try
        {
            if (p == null) return "?";
            if (!string.IsNullOrEmpty(p.steamUsername)) return p.steamUsername;
            if (p.character != null) return (p.character.Firstname + " " + p.character.Lastname).Trim();
        }
        catch
        {
        }
        return "?";
    }

    // Supprime les fichiers journaliers plus vieux que retentionDays
    private static void CleanupOldLogs(string dir, string prefix, int retentionDays)
    {
        try
        {
            if (dir == null || !Directory.Exists(dir))
            {
                return;
            }
            DateTime limit = DateTime.Now.Date.AddDays(-retentionDays);
            foreach (string f in Directory.GetFiles(dir, prefix + "*.log"))
            {
                Match m = Regex.Match(Path.GetFileName(f), prefix + @"(\d{4}-\d{2}-\d{2})\.log$");
                DateTime d;
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, out d) && d < limit)
                {
                    File.Delete(f);
                    Debug.Log("[TKWEB] Log purgé (> " + retentionDays + " j) : " + Path.GetFileName(f));
                }
            }
        }
        catch
        {
        }
    }

    private void InitActivity()
    {
        try
        {
            actDir = Path.Combine(pluginDir, "logs");
            if (!Directory.Exists(actDir))
            {
                Directory.CreateDirectory(actDir);
            }
            CleanupOldLogs(actDir, "activity-", config.logRetentionDays);
            if (Nova.server == null)
            {
                return;
            }
            Nova.server.OnPlayerKillPlayerEvent += delegate (Player killer, Player victim)
            {
                try
                {
                    RecordActivity("KILL", PseudoOf(killer), killer != null ? killer.steamId.ToString() : "",
                        "tue " + PseudoOf(victim) + (victim != null ? " (" + victim.steamId + ")" : ""));
                }
                catch { }
            };
            Nova.server.OnPlayerDamagePlayerEvent += delegate (Player attacker, Player victim, int dmg)
            {
                try
                {
                    if (dmg >= 20) // ignore les petits coups pour ne pas noyer le journal
                    {
                        RecordActivity("DEGATS", PseudoOf(attacker), attacker != null ? attacker.steamId.ToString() : "",
                            "-" + dmg + " PV sur " + PseudoOf(victim));
                    }
                }
                catch { }
            };
            Nova.server.OnPlayerSellDrugsEvent += delegate (Player p, int a, int b)
            {
                try
                {
                    RecordActivity("DROGUE", PseudoOf(p), p != null ? p.steamId.ToString() : "",
                        "vente de drogue (montant " + a + ", qté " + b + ")");
                }
                catch { }
            };
            Nova.server.OnPlayerConsumeDrugEvent += delegate (Player p)
            {
                try
                {
                    RecordActivity("DROGUE", PseudoOf(p), p != null ? p.steamId.ToString() : "", "consomme de la drogue");
                }
                catch { }
            };
            Nova.server.OnPlayerUseCommandEvent += delegate (Player p, SChatCommand cmd)
            {
                try
                {
                    RecordActivity("COMMANDE", PseudoOf(p), p != null ? p.steamId.ToString() : "",
                        "/" + (cmd != null ? cmd.fullCommandName : "?"));
                }
                catch { }
            };
            Debug.Log("[TKWEB] Journal d'activité branché (kills, dégâts, drogue, commandes)");
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Erreur init journal d'activité : " + ex.Message);
        }
    }

    private void InitChat()
    {
        try
        {
            chatDir = Path.Combine(pluginDir, "chat");
            if (!Directory.Exists(chatDir))
            {
                Directory.CreateDirectory(chatDir);
            }
            CleanupOldLogs(chatDir, "chat-", config.logRetentionDays);
            // recharge la fin du fichier du jour pour garder l'historique après restart
            string today = Path.Combine(chatDir, "chat-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            if (File.Exists(today))
            {
                string[] lines = File.ReadAllLines(today);
                int start = Math.Max(0, lines.Length - 200);
                for (int i = start; i < lines.Length; i++)
                {
                    Match m = Regex.Match(lines[i], @"^\[(?<t>[\d:]+)\] (?<p>.*) \((?<s>\d*)\) : (?<x>.*)$");
                    if (!m.Success)
                    {
                        continue;
                    }
                    lock (chatLock)
                    {
                        chatLastId++;
                        chatRing.Add("{\"id\":" + chatLastId + ",\"time\":" + Json.Str(m.Groups["t"].Value)
                            + ",\"pseudo\":" + Json.Str(m.Groups["p"].Value) + ",\"steamId\":\"" + m.Groups["s"].Value + "\""
                            + ",\"text\":" + Json.Str(m.Groups["x"].Value) + "}");
                    }
                }
                Debug.Log("[TKWEB] Historique de chat rechargé (" + chatRing.Count + " messages du jour)");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Erreur init chat : " + ex.Message);
        }
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        if (!config.enabled)
        {
            Debug.Log("[TKWEB] Plugin TKWebPanel v2.0 désactivé par config");
            return;
        }
        try
        {
            GameObject go = new GameObject("TKWebPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            dispatcher = go.AddComponent<TKWebPanelDispatcher>();
            dispatcher.allocatedCores = config.allocatedCores;

            int port = ResolvePort();
            listener = new HttpListener();
            listener.Prefixes.Add("http://*:" + port + "/");
            listener.Start();
            httpThread = new Thread(HttpLoop);
            httpThread.IsBackground = true;
            httpThread.Name = "TKWebPanel-HTTP";
            httpThread.Start();
            InitChat();
            InitActivity();
            usersPath = Path.Combine(pluginDir, "users.json");
            LoadPanelUsers();
            StartAutoBackup();
        StartSericache();
        StartIdentityLogger();
        StartLogBuffer();
            Debug.Log("[TKWEB] Plugin TKWebPanel v3.7.1 initialisé — panel sur le port " + port);
            AnnounceUrl(port);
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Impossible de démarrer le serveur HTTP : " + ex.Message);
        }
    }

    private void LoadConfig()
    {
        try
        {
            pluginDir = Path.Combine(pluginsPath, "TKWebPanel");
            if (!Directory.Exists(pluginDir))
            {
                Directory.CreateDirectory(pluginDir);
            }
            string configPath = Path.Combine(pluginDir, "config.json");
            if (!File.Exists(configPath))
            {
                config = new TKWebPanelConfig();
            }
            else
            {
                config = TKWebPanelConfig.FromJson(File.ReadAllText(configPath));
            }
            if (string.IsNullOrEmpty(config.password))
            {
                config.password = GeneratePassword();
                Debug.Log("[TKWEB] Mot de passe généré (voir Plugins/TKWebPanel/config.json)");
            }
            File.WriteAllText(configPath, TKWebPanelConfig.ToJson(config));
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Erreur chargement config : " + ex.Message);
            config = new TKWebPanelConfig();
            if (string.IsNullOrEmpty(config.password))
            {
                config.password = GeneratePassword();
            }
        }
    }

    // Affiche l'URL du panel dans la console AMP (bannière verte ANSI si activé).
    // L'IP publique est détectée en arrière-plan (api.ipify.org, timeout court).
    private void AnnounceUrl(int port)
    {
        Thread t = new Thread(delegate ()
        {
            if (!string.IsNullOrEmpty(config.publicUrl))
            {
                string g0 = config.ansiColors ? "[92m" : "";
                string z0 = config.ansiColors ? "[0m" : "";
                Debug.Log(g0 + "[TKWEB] ============================================================" + z0);
                Debug.Log(g0 + "[TKWEB]   Panel admin web : " + config.publicUrl + z0);
                Debug.Log(g0 + "[TKWEB]   Mot de passe    : Plugins/TKWebPanel/config.json" + z0);
                Debug.Log(g0 + "[TKWEB] ============================================================" + z0);
                return;
            }
            string host = config.publicHost;
            if (string.IsNullOrEmpty(host))
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                    req.Timeout = 4000;
                    using (StreamReader r = new StreamReader(req.GetResponse().GetResponseStream()))
                    {
                        host = r.ReadToEnd().Trim();
                    }
                }
                catch
                {
                    host = "IP-du-serveur";
                }
            }
            string g = config.ansiColors ? "[92m" : "";
            string z = config.ansiColors ? "[0m" : "";
            Debug.Log(g + "[TKWEB] ============================================================" + z);
            Debug.Log(g + "[TKWEB]   Panel admin web : http://" + host + ":" + port + z);
            Debug.Log(g + "[TKWEB]   Mot de passe    : Plugins/TKWebPanel/config.json" + z);
            Debug.Log(g + "[TKWEB] ============================================================" + z);
        });
        t.IsBackground = true;
        t.Start();
    }

    // port 0 (auto) = port du jeu + 4 (7787 -> 7791). Évite tout conflit
    // quand plusieurs instances Nova-Life tournent sur la même machine.
    private int ResolvePort()
    {
        if (config.port > 0)
        {
            return config.port;
        }
        int serverPort = 7787;
        try
        {
            string cfg = Path.Combine(Path.GetDirectoryName(pluginDir), "../Config/server.json");
            if (File.Exists(cfg))
            {
                serverPort = Json.GetInt(File.ReadAllText(cfg), "serverPort", serverPort);
            }
        }
        catch
        {
        }
        return serverPort + 4;
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        StringBuilder sb = new StringBuilder();
        System.Random rng = new System.Random(Guid.NewGuid().GetHashCode());
        for (int i = 0; i < 14; i++)
        {
            sb.Append(chars[rng.Next(chars.Length)]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Boucle HTTP (threads d'arrière-plan)
    // ------------------------------------------------------------------
    private void HttpLoop()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = listener.GetContext();
            }
            catch
            {
                break;
            }
            ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string responseText = "";
        string contentType = "application/json; charset=utf-8";
        int status = 200;
        try
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (path == "/" || path == "/index.html")
            {
                responseText = Encoding.UTF8.GetString(Convert.FromBase64String(TKWebPanelPage.Base64));
                contentType = "text/html; charset=utf-8";
            }
            else if (path.StartsWith("/icon/"))
            {
                ServeIcon(ctx, path, "icons");
                return;
            }
            else if (path.StartsWith("/vicon/"))
            {
                ServeIcon(ctx, path, "vehicons");
                return;
            }
            else if (path.StartsWith("/img/"))
            {
                ServeSerigraphie(ctx, path);
                return;
            }
            else if (path == "/mapimg")
            {
                ServeMapImage(ctx);
                return;
            }
            else if (path.StartsWith("/api/"))
            {
                string body = "";
                if (ctx.Request.HasEntityBody)
                {
                    using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                }
                responseText = HandleApi(ctx, path, body, ref status);
            }
            else
            {
                status = 404;
                responseText = "{\"error\":\"not found\"}";
            }
        }
        catch (TimeoutException)
        {
            status = 503;
            responseText = "{\"error\":\"serveur occupé, réessayez\"}";
        }
        catch (Exception ex)
        {
            status = 500;
            responseText = "{\"error\":" + Json.Str(ex.Message) + "}";
        }
        try
        {
            byte[] buffer = Encoding.UTF8.GetBytes(responseText);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            // le HTML et l'API ne doivent jamais être mis en cache par le
            // navigateur (sinon un ancien panel reste affiché après une maj)
            try { ctx.Response.Headers["Cache-Control"] = "no-store, must-revalidate"; } catch { }
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
        catch
        {
        }
    }

    // Sert une icône d'item PNG depuis Plugins/TKWebPanel/icons/{id}.png
    // (icônes extraites du jeu). Cache navigateur 7 jours. Pas d'auth : ce ne
    // sont que des images d'items, et ça permet le cache/preload simple.
    private void ServeIcon(HttpListenerContext ctx, string path, string folder)
    {
        try
        {
            string name = Path.GetFileName(path); // "123.png"
            if (!Regex.IsMatch(name, @"^\d+\.png$"))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.OutputStream.Close();
                return;
            }
            string file = Path.Combine(Path.Combine(pluginDir, folder), name);
            if (!File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.OutputStream.Close();
                return;
            }
            byte[] data = File.ReadAllBytes(file);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/png";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=604800";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // Comptes panel à rôles (v2.3) : owner(3) > admin(2) > modo(1)
    // Le mot de passe de config.json = compte "owner" implicite.
    // Comptes supplémentaires dans Plugins/TKWebPanel/users.json
    // ------------------------------------------------------------------
    private class PanelUser
    {
        public string name;
        public string password;
        public int level; // 1 modo, 2 admin, 3 owner
    }

    private static readonly object usersLock = new object();
    private static List<PanelUser> panelUsers = new List<PanelUser>();
    private static string usersPath;

    private static void LoadPanelUsers()
    {
        lock (usersLock)
        {
            panelUsers.Clear();
            try
            {
                if (usersPath != null && File.Exists(usersPath))
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(usersPath),
                        "\\{\\s*\"name\"\\s*:\\s*\"(?<n>(?:\\\\.|[^\"])*)\"\\s*,\\s*\"password\"\\s*:\\s*\"(?<p>(?:\\\\.|[^\"])*)\"\\s*,\\s*\"level\"\\s*:\\s*(?<l>\\d)"))
                    {
                        panelUsers.Add(new PanelUser
                        {
                            name = m.Groups["n"].Value,
                            password = m.Groups["p"].Value,
                            level = int.Parse(m.Groups["l"].Value)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[TKWEB] Erreur lecture users.json : " + ex.Message);
            }
        }
    }

    private static void SavePanelUsers()
    {
        lock (usersLock)
        {
            try
            {
                StringBuilder sb = new StringBuilder("[\n");
                for (int i = 0; i < panelUsers.Count; i++)
                {
                    sb.Append("  {\"name\": ").Append(Json.Str(panelUsers[i].name))
                      .Append(", \"password\": ").Append(Json.Str(panelUsers[i].password))
                      .Append(", \"level\": ").Append(panelUsers[i].level).Append("}");
                    if (i < panelUsers.Count - 1) sb.Append(",");
                    sb.Append("\n");
                }
                sb.Append("]\n");
                File.WriteAllText(usersPath, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError("[TKWEB] Erreur écriture users.json : " + ex.Message);
            }
        }
    }

    // Renvoie le niveau du compte correspondant au mot de passe (0 = refus)
    private int AuthLevel(string provided, out string userName)
    {
        userName = "";
        if (string.IsNullOrEmpty(provided))
        {
            return 0;
        }
        if (SlowEquals(provided, config.password))
        {
            userName = "owner";
            return 3;
        }
        lock (usersLock)
        {
            foreach (PanelUser u in panelUsers)
            {
                if (SlowEquals(provided, u.password))
                {
                    userName = u.name;
                    return u.level;
                }
            }
        }
        return 0;
    }

    private int CheckAuthLevel(HttpListenerContext ctx, out string userName)
    {
        return AuthLevel(ctx.Request.Headers["X-Auth"], out userName);
    }

    // Niveau minimal requis par endpoint (1 modo, 2 admin, 3 owner)
    private static int MinLevel(string path)
    {
        switch (path)
        {
            // owner uniquement
            case "/api/setadmin":
            case "/api/panelusers":
            case "/api/paneluserset":
            case "/api/paneluserdel":
            case "/api/backups":
            case "/api/backupnow":
            case "/api/acconfig":
            case "/api/acset":
            case "/api/plugconfig":
            case "/api/plugset":
            case "/api/acwl":
            case "/api/benchspawn":
            case "/api/benchghost":
            case "/api/benchreal":
            case "/api/benchclear":
            case "/api/benchstatus":
            case "/api/banip":
            case "/api/banipraw":
            case "/api/serverlog":
            case "/api/identity":
                return 3;
            // modo autorisé (consultation + modération légère)
            case "/api/status":
            case "/api/players":
            case "/api/kick":
            case "/api/message":
            case "/api/notify":
            case "/api/chat":
            case "/api/chatsend":
            case "/api/chathistory":
            case "/api/activity":
            case "/api/activityhistory":
            case "/api/accheck":
            case "/api/history":
            case "/api/playercard":
            case "/api/checkban":
            case "/api/inventory":
            case "/api/offlineinv":
            case "/api/vehicles":
            case "/api/offlinevehicles":
            case "/api/items":
            case "/api/vehiclemodels":
            case "/api/areas":
            case "/api/bizs":
            case "/api/fps":       // GET seulement, le POST est filtré plus bas
            case "/api/mapcalib":  // GET seulement, le POST (calibrer) est filtré plus bas
            case "/api/mapvehicles":
            case "/api/ghoststats":
            case "/api/heavyareas":
            case "/api/floodbans":
            case "/api/acdismissed":
            case "/api/rankings":
            case "/api/admins":
                return 1;
            // tout le reste : admin
            default:
                return 2;
        }
    }

    private static bool SlowEquals(string a, string b)
    {
        if (a == null || b == null)
        {
            return false;
        }
        int diff = a.Length ^ b.Length;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }
        return diff == 0;
    }

    private string HandleApi(HttpListenerContext ctx, string path, string body, ref int status)
    {
        if (path == "/api/login")
        {
            string pass = Json.GetString(body, "password", "");
            string loginName;
            int loginLevel = AuthLevel(pass, out loginName);
            if (loginLevel > 0)
            {
                return "{\"ok\":true,\"name\":" + Json.Str(loginName) + ",\"level\":" + loginLevel + "}";
            }
            Thread.Sleep(800); // freine le brute-force
            status = 401;
            return "{\"error\":\"mot de passe incorrect\"}";
        }

        string authUser;
        int level = CheckAuthLevel(ctx, out authUser);
        if (level == 0)
        {
            status = 401;
            return "{\"error\":\"non authentifié\"}";
        }
        panelActor = authUser;
        int need = MinLevel(path);
        if (path == "/api/fps" && ctx.Request.HttpMethod != "GET")
        {
            need = 2; // modifier les FPS = admin
        }
        if (path == "/api/mapcalib" && ctx.Request.HttpMethod != "GET")
        {
            need = 2; // calibrer la carte = admin
        }
        if (level < need)
        {
            status = 403;
            return "{\"error\":\"permission insuffisante (réservé " + (need >= 3 ? "au propriétaire" : "aux admins") + ")\"}";
        }

        switch (path)
        {
            case "/api/status":
                return (string)RunOnMain(ApiStatus);
            case "/api/players":
                return (string)RunOnMain(ApiPlayers);
            case "/api/kick":
                return ApiKick(body);
            case "/api/ban":
                return ApiBan(body);
            case "/api/unban":
                return ApiUnban(body);
            case "/api/checkban":
                return ApiCheckBan(ctx.Request.QueryString["steamId"]);
            case "/api/money":
                return ApiMoney(body);
            case "/api/message":
                return ApiMessage(body);
            case "/api/announce":
                return ApiAnnounce(body);
            case "/api/stats":
                return ApiStats(body);
            case "/api/heal":
                return ApiHeal(body);
            case "/api/tp":
                return ApiTeleport(body);
            case "/api/bring":
                return ApiBring(body);
            case "/api/accheck":
                return ApiAntiCheat();
            case "/api/acconfig":
                return ApiAcConfig();
            case "/api/acset":
                return ApiAcSet(body);
            case "/api/plugconfig":
                return ApiPlugConfig(ctx.Request.QueryString["name"]);
            case "/api/plugset":
                return ApiPlugSet(body);
            case "/api/acwl":
                return ApiAcWl(body);
            case "/api/mapcalib":
                if (ctx.Request.HttpMethod == "POST")
                {
                    return ApiMapCalibSet(body);
                }
                return ApiMapCalibGet();
            case "/api/mapvehicles":
                return ApiMapVehicles();
            case "/api/acdismissed":
                return ApiAcDismissed();
            case "/api/rankings":
                return ApiRankings();
            case "/api/acdismiss":
                return ApiAcDismiss(body);
            case "/api/banip":
                return ApiBanIp(body);
            case "/api/banipraw":
                return ApiBanIpRaw(body);
            case "/api/serverlog":
                return ApiServerLog(body);
            case "/api/identity":
                return ApiIdentity(ctx.Request.QueryString["steamId"], ctx.Request.QueryString["ip"]);
            case "/api/benchspawn":
                return ApiBenchSpawn(body);
            case "/api/benchghost":
                return ApiBenchGhost();
            case "/api/benchreal":
                return ApiBenchReal();
            case "/api/benchclear":
                return ApiBenchClear();
            case "/api/benchstatus":
                return ApiBenchStatus();
            case "/api/history":
                return ApiHistory();
            case "/api/offlineinv":
                return ApiOfflineInventory(ctx.Request.QueryString["characterId"]);
            case "/api/offlineremoveitem":
                return ApiOfflineRemoveItem(body);
            case "/api/offlinevehicles":
                return ApiOfflineVehicles(ctx.Request.QueryString["characterId"]);
            case "/api/playercard":
                return ApiPlayerCard(ctx.Request.QueryString["characterId"]);
            case "/api/areas":
                return ApiPlayerAreas(ctx.Request.QueryString["characterId"]);
            case "/api/bizs":
                return ApiBizs();
            case "/api/heavyareas":
                return ApiHeavyAreas();
            case "/api/setarealimit":
                return ApiSetAreaLimit(body);
            case "/api/notifyareaowner":
                return ApiNotifyAreaOwner(body);
            case "/api/chat":
                return ApiChat(ctx.Request.QueryString["after"]);
            case "/api/chatsend":
                return ApiChatSend(body);
            case "/api/chathistory":
                return ApiChatHistory(ctx.Request.QueryString["date"]);
            case "/api/admins":
                return ApiAdmins();
            case "/api/setadmin":
                return ApiSetAdmin(body);
            case "/api/notify":
                return ApiNotify(body);
            case "/api/prison":
                return ApiPrison(body);
            case "/api/givexp":
                return ApiGiveXp(body);
            case "/api/activity":
                return ApiActivity(ctx.Request.QueryString["after"], ctx.Request.QueryString["kind"]);
            case "/api/activityhistory":
                return ApiActivityHistory(ctx.Request.QueryString["date"]);
            case "/api/msgadmins":
                return ApiMsgAdmins(body);
            case "/api/localmsg":
                return ApiLocalMsg(body);
            case "/api/permis":
                return ApiPermis(body);
            case "/api/sms":
                return ApiSms(ctx.Request.QueryString["characterId"]);
            case "/api/contacts":
                return ApiContacts(ctx.Request.QueryString["characterId"]);
            case "/api/mails":
                return ApiMails();
            case "/api/panelusers":
                return ApiPanelUsers();
            case "/api/paneluserset":
                return ApiPanelUserSet(body);
            case "/api/paneluserdel":
                return ApiPanelUserDel(body);
            case "/api/ghoststats":
                return (string)RunOnMain(ApiGhostStats);
            case "/api/plugins":
                return ApiPlugins();
            case "/api/backups":
                return ApiBackups();
            case "/api/backupnow":
                return ApiBackupNow();
            case "/api/floodbans":
                return ApiFloodBans();
            case "/api/floodunban":
                return ApiFloodUnban(body);
            case "/api/fps":
                return ctx.Request.HttpMethod == "GET" ? (string)RunOnMain(ApiFpsGet) : ApiFpsSet(body);
            case "/api/items":
                return (string)RunOnMain(ApiItems);
            case "/api/giveitem":
                return ApiGiveItem(body, true);
            case "/api/removeitem":
                return ApiGiveItem(body, false);
            case "/api/inventory":
                return ApiInventory(ctx.Request.QueryString["steamId"]);
            case "/api/vehiclemodels":
                return (string)RunOnMain(ApiVehicleModels);
            case "/api/vehicles":
                return ApiVehicles(ctx.Request.QueryString["steamId"]);
            case "/api/givevehicle":
                return ApiGiveVehicle(body);
            case "/api/vehiclestow":
                return ApiVehicleStow(body);
            case "/api/vehicleunstow":
                return ApiVehicleUnstow(body);
            case "/api/vehicledelete":
                return ApiVehicleDelete(body);
            default:
                status = 404;
                return "{\"error\":\"endpoint inconnu\"}";
        }
    }

    // Exécute fn sur le thread principal Unity et attend le résultat
    private static object RunOnMain(Func<object> fn)
    {
        if (dispatcher == null)
        {
            throw new TimeoutException();
        }
        object result = null;
        Exception error = null;
        using (ManualResetEventSlim done = new ManualResetEventSlim(false))
        {
            dispatcher.Enqueue(delegate
            {
                try
                {
                    result = fn();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });
            if (!done.Wait(8000))
            {
                throw new TimeoutException();
            }
        }
        if (error != null)
        {
            throw error;
        }
        return result;
    }

    private static Player FindPlayer(string steamId)
    {
        if (Nova.server == null || string.IsNullOrEmpty(steamId))
        {
            return null;
        }
        foreach (Player p in Nova.server.GetAllPlayers())
        {
            if (p != null && p.steamId.ToString() == steamId)
            {
                return p;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Endpoints
    // ------------------------------------------------------------------
    private object ApiStatus()
    {
        int players = Nova.server != null ? Nova.server.PlayerCount : 0;
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"serverName\":").Append(Json.Str(GetServerName())).Append(",");
        sb.Append("\"players\":").Append(players).Append(",");
        sb.Append("\"slots\":").Append(GetServerSlots()).Append(",");
        sb.Append("\"uptimeSeconds\":").Append((long)Time.realtimeSinceStartup).Append(",");
        sb.Append("\"targetFps\":").Append(Application.targetFrameRate).Append(",");
        sb.Append("\"actualFps\":").Append(dispatcher != null ? dispatcher.ActualFps.ToString("0") : "0").Append(",");
        sb.Append("\"cpuPercent\":").Append(dispatcher != null ? dispatcher.CpuPercent.ToString("0") : "-1").Append(",");
        sb.Append("\"memoryMb\":").Append(GC.GetTotalMemory(false) / 1048576L);
        sb.Append("}");
        return sb.ToString();
    }

    private string cachedServerName;
    private int cachedSlots = -1;

    private string GetServerName()
    {
        if (cachedServerName == null)
        {
            cachedServerName = "Serveur Nova-Life";
            try
            {
                string cfg = Path.Combine(Path.GetDirectoryName(pluginDir), "../Config/server.json");
                if (File.Exists(cfg))
                {
                    string json = File.ReadAllText(cfg);
                    cachedServerName = Json.GetString(json, "serverName", cachedServerName);
                    cachedSlots = Json.GetInt(json, "serverSlot", 50);
                }
            }
            catch
            {
            }
        }
        return cachedServerName;
    }

    private int GetServerSlots()
    {
        if (cachedSlots < 0)
        {
            GetServerName();
            if (cachedSlots < 0)
            {
                cachedSlots = 50;
            }
        }
        return cachedSlots;
    }

    private object ApiPlayers()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        bool first = true;
        if (Nova.server != null)
        {
            foreach (Player p in Nova.server.GetAllPlayers())
            {
                if (p == null)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                string firstname = "";
                string lastname = "";
                double money = 0;
                double bank = 0;
                int health = 0;
                int adminLevel = 0;
                float x = 0f, y = 0f, z = 0f;
                bool inGame = false;
                try { adminLevel = p.account != null ? p.account.adminLevel : 0; } catch { }
                try
                {
                    if (p.character != null)
                    {
                        firstname = p.character.Firstname ?? "";
                        lastname = p.character.Lastname ?? "";
                        bank = p.character.Bank;
                    }
                }
                catch { }
                try
                {
                    if (p.setup != null)
                    {
                        inGame = true;
                        money = p.Money;
                        health = p.Health;
                        Vector3 pos = p.setup.transform.position;
                        x = pos.x; y = pos.y; z = pos.z;
                    }
                }
                catch { }
                sb.Append("{");
                sb.Append("\"steamId\":\"").Append(p.steamId).Append("\",");
                sb.Append("\"pseudo\":").Append(Json.Str(p.steamUsername ?? "")).Append(",");
                sb.Append("\"firstname\":").Append(Json.Str(firstname)).Append(",");
                sb.Append("\"lastname\":").Append(Json.Str(lastname)).Append(",");
                sb.Append("\"money\":").Append(money.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"bank\":").Append(bank.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"health\":").Append(health).Append(",");
                sb.Append("\"adminLevel\":").Append(adminLevel).Append(",");
                sb.Append("\"inGame\":").Append(inGame ? "true" : "false").Append(",");
                sb.Append("\"x\":").Append(x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"y\":").Append(y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"z\":").Append(z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("}");
            }
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string ApiKick(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string reason = Json.GetString(body, "reason", "Kick administratif");
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null)
            {
                return "{\"error\":\"joueur introuvable\"}";
            }
            try { if (p.account != null) { p.account.kicks++; LifeDB.SaveAccount(p.account); } } catch { }
            p.Disconnect("Kick : " + reason);
            Debug.Log("[TKWEB] KICK steamid=" + steamId + " raison=\"" + reason + "\"");
            StaffLog("kick de " + steamId + " (" + reason + ")");
            return "{\"ok\":true}";
        });
    }

    private string ApiBan(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string reason = Json.GetString(body, "reason", "Ban administratif");
        int minutes = Json.GetInt(body, "minutes", 0); // 0 = permanent
        long until = minutes <= 0 ? -1L : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + minutes * 60L;

        // Joueur en ligne ?
        string onlineResult = (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.account == null)
            {
                return null;
            }
            p.account.banTimestamp = until;
            p.account.banReason = reason;
            p.account.bans++;
            LifeDB.SaveAccount(p.account);
            p.Disconnect("Banni : " + reason);
            Debug.Log("[TKWEB] BAN steamid=" + steamId + " duree=" + (minutes <= 0 ? "permanent" : minutes + " min") + " raison=\"" + reason + "\"");
            StaffLog("ban de " + steamId + " " + (minutes <= 0 ? "permanent" : minutes + " min") + " (" + reason + ")");
            return "{\"ok\":true,\"online\":true}";
        });
        if (onlineResult != null)
        {
            return onlineResult;
        }

        // Hors-ligne : via la base
        Account account = LifeDB.FetchAccount(steamId).Result;
        if (account == null)
        {
            return "{\"error\":\"aucun compte avec ce SteamID\"}";
        }
        account.banTimestamp = until;
        account.banReason = reason;
        account.bans++;
        bool saved = LifeDB.SaveAccount(account).Result;
        Debug.Log("[TKWEB] BAN (offline) steamid=" + steamId + " duree=" + (minutes <= 0 ? "permanent" : minutes + " min") + " raison=\"" + reason + "\"");
        StaffLog("ban (hors ligne) de " + steamId + " " + (minutes <= 0 ? "permanent" : minutes + " min") + " (" + reason + ")");
        return saved ? "{\"ok\":true,\"online\":false}" : "{\"error\":\"échec sauvegarde\"}";
    }

    private string ApiUnban(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        Account account = LifeDB.FetchAccount(steamId).Result;
        if (account == null)
        {
            return "{\"error\":\"aucun compte avec ce SteamID\"}";
        }
        account.banTimestamp = 0L;
        account.banReason = "";
        bool saved = LifeDB.SaveAccount(account).Result;
        Debug.Log("[TKWEB] UNBAN steamid=" + steamId);
        StaffLog("deban de " + steamId);
        return saved ? "{\"ok\":true}" : "{\"error\":\"échec sauvegarde\"}";
    }

    private string ApiCheckBan(string steamId)
    {
        if (string.IsNullOrEmpty(steamId))
        {
            return "{\"error\":\"steamId manquant\"}";
        }
        Account account = LifeDB.FetchAccount(steamId).Result;
        if (account == null)
        {
            return "{\"error\":\"aucun compte avec ce SteamID\"}";
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool banned = account.banTimestamp == -1 || account.banTimestamp > now;
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"banned\":").Append(banned ? "true" : "false");
        sb.Append(",\"permanent\":").Append(account.banTimestamp == -1 ? "true" : "false");
        sb.Append(",\"until\":").Append(account.banTimestamp);
        sb.Append(",\"reason\":").Append(Json.Str(account.banReason ?? ""));
        sb.Append(",\"username\":").Append(Json.Str(account.username ?? ""));
        sb.Append(",\"totalBans\":").Append(account.bans);
        sb.Append(",\"totalKicks\":").Append(account.kicks);
        sb.Append("}");
        return sb.ToString();
    }

    private string ApiMoney(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string target = Json.GetString(body, "target", "cash");
        double amount = Json.GetDouble(body, "amount", 0);
        if (amount == 0)
        {
            return "{\"error\":\"montant nul\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            if (target == "bank")
            {
                p.AddBankMoney(amount, "Panel admin TKWebPanel");
            }
            else
            {
                p.AddMoney(amount, "Panel admin TKWebPanel");
            }
            Debug.Log("[TKWEB] MONEY steamid=" + steamId + " " + target + " " + (amount > 0 ? "+" : "") + amount);
            StaffLog("argent " + (amount > 0 ? "+" : "") + amount.ToString("0") + " (" + target + ") a " + steamId);
            return "{\"ok\":true}";
        });
    }

    private string ApiMessage(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string text = Json.GetString(body, "text", "");
        if (string.IsNullOrEmpty(text))
        {
            return "{\"error\":\"message vide\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null)
            {
                return "{\"error\":\"joueur introuvable\"}";
            }
            p.SendText("<color=#ff8800>[ADMIN]</color> " + text);
            return "{\"ok\":true}";
        });
    }

    private string ApiAnnounce(string body)
    {
        string text = Json.GetString(body, "text", "");
        if (string.IsNullOrEmpty(text))
        {
            return "{\"error\":\"message vide\"}";
        }
        return (string)RunOnMain(delegate
        {
            if (Nova.server == null)
            {
                return "{\"error\":\"serveur indisponible\"}";
            }
            Nova.server.SendMessageToAll("<color=#ff8800>[ANNONCE]</color> <color=#ffffff>" + text + "</color>");
            Debug.Log("[TKWEB] ANNONCE \"" + text + "\"");
            StaffLog("annonce : " + text);
            return "{\"ok\":true}";
        });
    }

    private string ApiHeal(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            p.Health = 100;
            p.SendText("<color=#00f0ff>Vous avez été soigné par un administrateur.</color>");
            Debug.Log("[TKWEB] HEAL steamid=" + steamId);
            StaffLog("soigne " + steamId);
            return "{\"ok\":true}";
        });
    }

    // Régler les jauges d'un joueur en ligne (vie / faim / soif), -1 = inchangé
    private string ApiStats(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int health = (int)Json.GetDouble(body, "health", -1);
        int hunger = (int)Json.GetDouble(body, "hunger", -1);
        int thirst = (int)Json.GetDouble(body, "thirst", -1);
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            if (health >= 0) p.Health = Math.Max(0, Math.Min(100, health));
            if (hunger >= 0) p.Hunger = Math.Max(0, Math.Min(100, hunger));
            if (thirst >= 0) p.Thirst = Math.Max(0, Math.Min(100, thirst));
            try { p.Notify("Admin", "Vos jauges ont été ajustées."); } catch { }
            Debug.Log("[TKWEB] STATS steamid=" + steamId + " vie=" + health + " faim=" + hunger + " soif=" + thirst);
            StaffLog("règle les jauges de " + steamId + " (vie=" + health + ", faim=" + hunger + ", soif=" + thirst + ")");
            return "{\"ok\":true}";
        });
    }

    private string ApiTeleport(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        double x = Json.GetDouble(body, "x", double.NaN);
        double y = Json.GetDouble(body, "y", double.NaN);
        double z = Json.GetDouble(body, "z", double.NaN);
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z))
        {
            return "{\"error\":\"coordonnées invalides\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            p.setup.TargetSetPosition(new Vector3((float)x, (float)y, (float)z));
            Debug.Log("[TKWEB] TP steamid=" + steamId + " -> " + x + "," + y + "," + z);
            StaffLog("teleporte " + steamId + " vers " + (int)x + "," + (int)y + "," + (int)z);
            return "{\"ok\":true}";
        });
    }

    private string ApiBring(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string targetSteamId = Json.GetString(body, "targetSteamId", "");
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            Player t = FindPlayer(targetSteamId);
            if (p == null || p.setup == null || t == null || t.setup == null)
            {
                return "{\"error\":\"joueur ou cible introuvable / pas en jeu\"}";
            }
            Vector3 pos = t.setup.transform.position;
            p.setup.TargetSetPosition(pos + new Vector3(1f, 0.5f, 1f));
            Debug.Log("[TKWEB] BRING steamid=" + steamId + " -> " + targetSteamId);
            StaffLog("teleporte " + steamId + " vers le joueur " + targetSteamId);
            return "{\"ok\":true}";
        });
    }

    // ------------------------------------------------------------------
    // FPS (pilote TKDynamicFps par réflexion ; repli direct sinon)
    // ------------------------------------------------------------------
    private static object FpsTickerInstance()
    {
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType("TKDynamicFpsTicker");
            if (t != null)
            {
                System.Reflection.FieldInfo f = t.GetField("Instance");
                return f != null ? f.GetValue(null) : null;
            }
        }
        return null;
    }

    private static object GetField(object obj, string name)
    {
        System.Reflection.FieldInfo f = obj.GetType().GetField(name);
        return f != null ? f.GetValue(obj) : null;
    }

    private static void SetField(object obj, string name, object value)
    {
        System.Reflection.FieldInfo f = obj.GetType().GetField(name);
        if (f != null)
        {
            f.SetValue(obj, value);
        }
    }

    private object ApiFpsGet()
    {
        object ticker = FpsTickerInstance();
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"available\":").Append(ticker != null ? "true" : "false");
        sb.Append(",\"target\":").Append(Application.targetFrameRate);
        sb.Append(",\"actual\":").Append(dispatcher != null ? dispatcher.ActualFps.ToString("0") : "0");
        if (ticker != null)
        {
            object cfg = GetField(ticker, "config");
            sb.Append(",\"forced\":").Append(GetField(ticker, "forcedFps"));
            if (cfg != null)
            {
                sb.Append(",\"idleFps\":").Append(GetField(cfg, "idleFps"));
                sb.Append(",\"minPlayersFps\":").Append(GetField(cfg, "minPlayersFps"));
                sb.Append(",\"maxFps\":").Append(GetField(cfg, "maxFps"));
            }
        }
        sb.Append("}");
        return sb.ToString();
    }

    private string ApiFpsSet(string body)
    {
        int force = Json.GetInt(body, "force", -2); // -2 = non fourni, -1 = repasser en auto
        int idle = Json.GetInt(body, "idleFps", 0);
        int minP = Json.GetInt(body, "minPlayersFps", 0);
        int maxF = Json.GetInt(body, "maxFps", 0);
        return (string)RunOnMain(delegate
        {
            object ticker = FpsTickerInstance();
            if (ticker == null)
            {
                // TKDynamicFps absent : action directe
                if (force > 0)
                {
                    Application.targetFrameRate = Mathf.Clamp(force, 10, 240);
                    return "{\"ok\":true,\"note\":\"TKDynamicFps absent, framerate appliqué directement\"}";
                }
                return "{\"error\":\"TKDynamicFps non chargé\"}";
            }
            if (force != -2)
            {
                SetField(ticker, "forcedFps", force > 0 ? Mathf.Clamp(force, 10, 240) : -1);
            }
            object cfg = GetField(ticker, "config");
            if (cfg != null)
            {
                if (idle >= 10) SetField(cfg, "idleFps", idle);
                if (minP >= 10) SetField(cfg, "minPlayersFps", minP);
                if (maxF >= 10) SetField(cfg, "maxFps", maxF);
                PersistFpsConfig(cfg);
            }
            Debug.Log("[TKWEB] FPS " + (force > 0 ? "forcé à " + force : (force == -1 ? "repassé en auto" : "config modifiée")));
            StaffLog("FPS " + (force > 0 ? "force a " + force : (force == -1 ? "repasse en auto" : "config modifiee")));
            return "{\"ok\":true}";
        });
    }

    private void PersistFpsConfig(object cfg)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(pluginDir), "TKDynamicFps");
            if (!Directory.Exists(dir))
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"enabled\": " + (((bool)GetField(cfg, "enabled")) ? "true" : "false") + ",");
            sb.AppendLine("  \"idleFps\": " + GetField(cfg, "idleFps") + ",");
            sb.AppendLine("  \"minPlayersFps\": " + GetField(cfg, "minPlayersFps") + ",");
            sb.AppendLine("  \"maxFps\": " + GetField(cfg, "maxFps") + ",");
            sb.AppendLine("  \"cpuHighPercent\": " + GetField(cfg, "cpuHighPercent") + ",");
            sb.AppendLine("  \"cpuLowPercent\": " + GetField(cfg, "cpuLowPercent") + ",");
            sb.AppendLine("  \"allocatedCores\": " + GetField(cfg, "allocatedCores") + ",");
            sb.AppendLine("  \"intervalSeconds\": " + GetField(cfg, "intervalSeconds") + ",");
            sb.AppendLine("  \"stepFps\": " + GetField(cfg, "stepFps") + ",");
            sb.AppendLine("  \"logChanges\": " + (((bool)GetField(cfg, "logChanges")) ? "true" : "false"));
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(dir, "config.json"), sb.ToString());
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Erreur persistance config FPS : " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Items
    // ------------------------------------------------------------------
    // itemName est souvent une clé de localisation non résolue côté serveur
    // headless (ex "1/Name") : on retombe alors sur le slug, plus lisible.
    private static string ReadableItemName(Item it)
    {
        if (it == null)
        {
            return "?";
        }
        string name = it.itemName;
        bool looksLikeKey = string.IsNullOrEmpty(name) || name.Contains("/") || name.EndsWith("/Name");
        if (looksLikeKey && !string.IsNullOrEmpty(it.slug))
        {
            return it.slug;
        }
        return string.IsNullOrEmpty(name) ? ("item " + it.id) : name;
    }

    private string cachedItemsJson;

    private object ApiItems()
    {
        if (cachedItemsJson != null)
        {
            return cachedItemsJson;
        }
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        bool first = true;
        try
        {
            Item[] items = Nova.man.item.items;
            for (int i = 0; i < items.Length; i++)
            {
                Item it = items[i];
                if (it == null)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("{\"id\":").Append(it.id);
                sb.Append(",\"name\":").Append(Json.Str(ReadableItemName(it)));
                sb.Append("}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Erreur liste items : " + ex.Message);
        }
        sb.Append("]");
        cachedItemsJson = sb.ToString();
        return cachedItemsJson;
    }

    private string ApiGiveItem(string body, bool give)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int itemId = Json.GetInt(body, "itemId", -1);
        int amount = Json.GetInt(body, "amount", 1);
        if (itemId < 0 || amount < 1)
        {
            return "{\"error\":\"item ou quantité invalide\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            bool ok = give
                ? p.setup.inventory.AddItem(itemId, amount, "")
                : p.setup.inventory.RemoveItem(itemId, amount, false);
            if (!ok)
            {
                return give ? "{\"error\":\"inventaire plein ou item invalide\"}" : "{\"error\":\"le joueur n'a pas assez de cet item\"}";
            }
            Debug.Log("[TKWEB] " + (give ? "GIVEITEM" : "REMOVEITEM") + " steamid=" + steamId + " item=" + itemId + " x" + amount);
            StaffLog((give ? "donne " : "retire ") + amount + "x item " + itemId + (give ? " a " : " de ") + steamId);
            return "{\"ok\":true}";
        });
    }

    private string ApiInventory(string steamId)
    {
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            foreach (ItemInventory slot in p.setup.inventory.items)
            {
                if (slot.itemId <= 0 || slot.number <= 0)
                {
                    continue;
                }
                string name = "item " + slot.itemId;
                try
                {
                    Item it = Nova.man.item.GetItem(slot.itemId);
                    if (it != null)
                    {
                        name = ReadableItemName(it);
                    }
                }
                catch
                {
                }
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("{\"itemId\":").Append(slot.itemId);
                sb.Append(",\"name\":").Append(Json.Str(name));
                sb.Append(",\"number\":").Append(slot.number).Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        });
    }

    // ------------------------------------------------------------------
    // Véhicules & garages
    // ------------------------------------------------------------------
    private object ApiVehicleModels()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        bool first = true;
        try
        {
            string[] names = Nova.v.vehiclesModelName;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i]))
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("{\"modelId\":").Append(i).Append(",\"name\":").Append(Json.Str(names[i])).Append("}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[TKWEB] Erreur liste modèles : " + ex.Message);
        }
        sb.Append("]");
        return sb.ToString();
    }

    private static string VehicleModelName(int modelId)
    {
        try
        {
            string[] names = Nova.v.vehiclesModelName;
            if (modelId >= 0 && modelId < names.Length && !string.IsNullOrEmpty(names[modelId]))
            {
                return names[modelId];
            }
        }
        catch
        {
        }
        return "modèle " + modelId;
    }

    private string ApiVehicles(string steamId)
    {
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.character == null)
            {
                return "{\"error\":\"joueur introuvable ou personnage non chargé\"}";
            }
            int charId = p.character.Id;
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            HashSet<int> seen = new HashSet<int>();
            foreach (LifeVehicle v in Nova.v.vehicles)
            {
                if (v == null || v.permissions == null || !seen.Add(v.vehicleId))
                {
                    continue; // null ou doublon en mémoire
                }
                bool owns = false;
                try { owns = v.permissions.HasPermission(charId); } catch { }
                if (!owns)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append("{\"vehicleId\":").Append(v.vehicleId);
                sb.Append(",\"modelId\":").Append(v.modelId);
                sb.Append(",\"name\":").Append(Json.Str(VehicleModelName(v.modelId)));
                sb.Append(",\"plate\":").Append(Json.Str(v.plate ?? ""));
                sb.Append(",\"isStowed\":").Append(v.isStowed ? "true" : "false");
                sb.Append(",\"fuel\":").Append(v.fuel.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        });
    }

    private string ApiGiveVehicle(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int modelId = Json.GetInt(body, "modelId", -1);
        if (modelId < 0)
        {
            return "{\"error\":\"modèle invalide\"}";
        }
        int charId = (int)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            return (object)(p != null && p.character != null ? p.character.Id : -1);
        });
        if (charId < 0)
        {
            return "{\"error\":\"joueur introuvable ou personnage non chargé\"}";
        }
        string permJson = "{\"owner\":{\"characterId\":" + charId + ",\"groupId\":0},\"coOwners\":[]}";
        Vehicles row = LifeDB.CreateVehicle(modelId, permJson).Result;
        if (row == null)
        {
            return "{\"error\":\"échec création en base\"}";
        }
        return (string)RunOnMain(delegate
        {
            // le jeu peut déjà avoir ajouté le véhicule à sa liste : pas de doublon
            if (Nova.v.GetVehicle(row.Id) != null)
            {
                Debug.Log("[TKWEB] GIVEVEHICLE steamid=" + steamId + " vehicleId=" + row.Id + " (déjà en mémoire)");
                StaffLog("donne le vehicule #" + row.Id + " a " + steamId);
                return "{\"ok\":true,\"vehicleId\":" + row.Id + ",\"note\":\"véhicule ajouté au garage du joueur\"}";
            }
            Nova.v.vehicles.Add(new LifeVehicle
            {
                modelId = row.ModelId,
                vehicleId = row.Id,
                permissions = JsonUtility.FromJson<Permissions>(row.Permissions),
                plate = row.Plate,
                isStowed = true,
                inventory = row.Inventory,
                engineInventory = row.EngineInventory,
                color = row.Color,
                smoothness = row.Smoothness,
                x = row.X,
                y = row.Y,
                z = row.Z,
                rotX = row.RotX,
                rotY = row.RotY,
                rotZ = row.RotZ,
                bizId = row.BizId,
                damages = row.Damages,
                fuel = row.Fuel,
                serigraphie = row.Serigraphie,
                eurosoftData = row.EurosoftData,
                accessoriesData = row.AccessoriesData
            });
            Debug.Log("[TKWEB] GIVEVEHICLE steamid=" + steamId + " model=" + modelId + " (" + VehicleModelName(modelId) + ") vehicleId=" + row.Id);
            StaffLog("donne le vehicule " + VehicleModelName(modelId) + " (#" + row.Id + ") a " + steamId);
            return "{\"ok\":true,\"vehicleId\":" + row.Id + ",\"note\":\"véhicule ajouté au garage du joueur\"}";
        });
    }

    private string ApiVehicleStow(string body)
    {
        int vehicleId = Json.GetInt(body, "vehicleId", -1);
        return (string)RunOnMain(delegate
        {
            LifeVehicle v = Nova.v.GetVehicle(vehicleId);
            if (v == null)
            {
                return "{\"error\":\"véhicule introuvable\"}";
            }
            Nova.v.StowVehicle(vehicleId);
            Debug.Log("[TKWEB] STOW vehicleId=" + vehicleId);
            StaffLog("range le vehicule #" + vehicleId + " au garage");
            return "{\"ok\":true}";
        });
    }

    private string ApiVehicleUnstow(string body)
    {
        int vehicleId = Json.GetInt(body, "vehicleId", -1);
        string steamId = Json.GetString(body, "steamId", "");
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu (le véhicule sort près du joueur)\"}";
            }
            LifeVehicle v = Nova.v.GetVehicle(vehicleId);
            if (v == null)
            {
                return "{\"error\":\"véhicule introuvable\"}";
            }
            if (!v.isStowed)
            {
                return "{\"error\":\"véhicule déjà sorti\"}";
            }
            Vector3 pos = p.setup.transform.position + new Vector3(3f, 0.5f, 3f);
            bool ok = Nova.v.UnstowVehicle(vehicleId, pos, Quaternion.identity);
            Debug.Log("[TKWEB] UNSTOW vehicleId=" + vehicleId + " près de " + steamId);
            StaffLog("sort le vehicule #" + vehicleId + " pres de " + steamId);
            return ok ? "{\"ok\":true}" : "{\"error\":\"échec de sortie du véhicule\"}";
        });
    }

    private string ApiVehicleDelete(string body)
    {
        int vehicleId = Json.GetInt(body, "vehicleId", -1);
        string rangerResult = (string)RunOnMain(delegate
        {
            LifeVehicle v = Nova.v.GetVehicle(vehicleId);
            if (v == null)
            {
                return "{\"error\":\"véhicule introuvable\"}";
            }
            if (!v.isStowed)
            {
                Nova.v.StowVehicle(vehicleId); // despawn du monde
            }
            return null;
        });
        if (rangerResult != null)
        {
            return rangerResult;
        }
        bool removed = LifeDB.RemoveVehicle(vehicleId).Result;
        return (string)RunOnMain(delegate
        {
            LifeVehicle v = Nova.v.GetVehicle(vehicleId);
            if (v != null)
            {
                Nova.v.vehicles.Remove(v);
            }
            Debug.Log("[TKWEB] DELETEVEHICLE vehicleId=" + vehicleId);
            StaffLog("supprime le vehicule #" + vehicleId);
            return removed ? "{\"ok\":true}" : "{\"error\":\"échec suppression en base\"}";
        });
    }

    // ------------------------------------------------------------------
    // Historique de TOUS les joueurs (même hors-ligne) : lecture directe de
    // life.db en lecture seule via sqlite-net embarqué dans le jeu.
    // ------------------------------------------------------------------
    public class HistoryRow
    {
        public string SteamId { get; set; }
        public string Username { get; set; }
        public int AdminLevel { get; set; }
        public long BanTimestamp { get; set; }
        public string BanReason { get; set; }
        public int BanCount { get; set; }
        public int KickCount { get; set; }
        public string Firstname { get; set; }
        public string Lastname { get; set; }
        public double Money { get; set; }
        public double Bank { get; set; }
        public int CharacterId { get; set; }
        public string Inventory { get; set; }
    }

    // L'argent liquide est stocké dans les portefeuilles (items dont data
    // contient currentMoney) : on le somme depuis le JSON d'inventaire.
    private static double WalletMoney(string inventoryJson)
    {
        if (string.IsNullOrEmpty(inventoryJson))
        {
            return 0;
        }
        double total = 0;
        foreach (Match m in Regex.Matches(inventoryJson, "currentMoney\\\\?\"?\\s*:\\s*(?<v>-?\\d+(\\.\\d+)?)"))
        {
            double v;
            if (double.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                total += v;
            }
        }
        return total;
    }

    private string ApiHistory()
    {
        string dbPath;
        try
        {
            dbPath = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "life.db"));
        }
        catch
        {
            return "{\"error\":\"chemin BDD introuvable\"}";
        }
        if (!File.Exists(dbPath))
        {
            return "{\"error\":\"life.db introuvable\"}";
        }
        HashSet<string> onlineIds = new HashSet<string>();
        try
        {
            if (Nova.server != null)
            {
                foreach (Player p in Nova.server.GetAllPlayers())
                {
                    if (p != null)
                    {
                        onlineIds.Add(p.steamId.ToString());
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(dbPath, SQLite.SQLiteOpenFlags.ReadOnly, false);
            try
            {
                // Un compte peut avoir plusieurs persos : on prend le plus riche.
                List<HistoryRow> rows = conn.Query<HistoryRow>(
                    "SELECT a.SteamId AS SteamId, a.Username AS Username, a.AdminLevel AS AdminLevel, " +
                    "a.BanTimestamp AS BanTimestamp, a.BanReason AS BanReason, a.BanCount AS BanCount, " +
                    "a.KickCount AS KickCount, c.Firstname AS Firstname, c.Lastname AS Lastname, " +
                    "c.Money AS Money, c.Bank AS Bank, c.Id AS CharacterId, c.Inventory AS Inventory " +
                    "FROM Accounts a LEFT JOIN Characters c ON c.AccountId = a.Id " +
                    "ORDER BY a.Id DESC LIMIT 2000");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                StringBuilder sb = new StringBuilder();
                sb.Append("[");
                bool first = true;
                foreach (HistoryRow r in rows)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    bool banned = r.BanTimestamp == -1 || r.BanTimestamp > now;
                    sb.Append("{\"steamId\":").Append(Json.Str(r.SteamId ?? ""));
                    sb.Append(",\"username\":").Append(Json.Str(r.Username ?? ""));
                    sb.Append(",\"firstname\":").Append(Json.Str(r.Firstname ?? ""));
                    sb.Append(",\"lastname\":").Append(Json.Str(r.Lastname ?? ""));
                    double cash = r.Money + WalletMoney(r.Inventory);
                    sb.Append(",\"money\":").Append(cash.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"bank\":").Append(r.Bank.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"characterId\":").Append(r.CharacterId);
                    sb.Append(",\"adminLevel\":").Append(r.AdminLevel);
                    sb.Append(",\"banned\":").Append(banned ? "true" : "false");
                    sb.Append(",\"banReason\":").Append(Json.Str(r.BanReason ?? ""));
                    sb.Append(",\"bans\":").Append(r.BanCount);
                    sb.Append(",\"kicks\":").Append(r.KickCount);
                    sb.Append(",\"online\":").Append(onlineIds.Contains(r.SteamId ?? "") ? "true" : "false");
                    sb.Append("}");
                }
                sb.Append("]");
                return sb.ToString();
            }
            finally
            {
                conn.Close();
            }
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str("lecture BDD : " + ex.Message) + "}";
        }
    }

    // ------------------------------------------------------------------
    // Gestion hors-ligne (lecture/écriture directe de life.db)
    // ------------------------------------------------------------------
    private string DbPath()
    {
        return Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "life.db"));
    }

    private class CharRow
    {
        public int Id { get; set; }
        public string Inventory { get; set; }
        public int AccountId { get; set; }
    }

    // Le perso appartient-il à un joueur actuellement en ligne ?
    private bool IsCharacterOnline(int characterId)
    {
        object result = RunOnMain(delegate
        {
            if (Nova.server == null)
            {
                return (object)false;
            }
            foreach (Player p in Nova.server.GetAllPlayers())
            {
                try
                {
                    if (p != null && p.character != null && p.character.Id == characterId)
                    {
                        return (object)true;
                    }
                }
                catch
                {
                }
            }
            return (object)false;
        });
        return (bool)result;
    }

    private static List<int[]> ParseInventory(string json)
    {
        // [ [itemId, number], ... ] dans l'ordre des slots (slots vides exclus)
        List<int[]> list = new List<int[]>();
        if (string.IsNullOrEmpty(json))
        {
            return list;
        }
        foreach (Match m in Regex.Matches(json, "\\{\\s*\"itemId\"\\s*:\\s*(?<i>\\d+)\\s*,\\s*\"number\"\\s*:\\s*(?<n>\\d+)"))
        {
            int id = int.Parse(m.Groups["i"].Value);
            int n = int.Parse(m.Groups["n"].Value);
            if (id > 0 && n > 0)
            {
                list.Add(new int[] { id, n });
            }
        }
        return list;
    }

    private string ApiOfflineInventory(string characterIdStr)
    {
        int characterId;
        if (!int.TryParse(characterIdStr ?? "", out characterId) || characterId <= 0)
        {
            return "{\"error\":\"characterId invalide\"}";
        }
        string inv;
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            List<CharRow> rows = conn.Query<CharRow>("SELECT Id, Inventory FROM Characters WHERE Id = ?", characterId);
            if (rows.Count == 0)
            {
                return "{\"error\":\"personnage introuvable\"}";
            }
            inv = rows[0].Inventory;
        }
        finally
        {
            conn.Close();
        }
        // agrège les slots du même item pour l'affichage
        Dictionary<int, int> totals = new Dictionary<int, int>();
        foreach (int[] slot in ParseInventory(inv))
        {
            int cur;
            totals.TryGetValue(slot[0], out cur);
            totals[slot[0]] = cur + slot[1];
        }
        object namesJson = RunOnMain(delegate
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            foreach (KeyValuePair<int, int> kv in totals)
            {
                string name = "item " + kv.Key;
                try
                {
                    Item it = Nova.man.item.GetItem(kv.Key);
                    if (it != null)
                    {
                        name = ReadableItemName(it);
                    }
                }
                catch
                {
                }
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"itemId\":").Append(kv.Key);
                sb.Append(",\"name\":").Append(Json.Str(name));
                sb.Append(",\"number\":").Append(kv.Value).Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        });
        return (string)namesJson;
    }

    private string ApiOfflineRemoveItem(string body)
    {
        int characterId = Json.GetInt(body, "characterId", -1);
        int itemId = Json.GetInt(body, "itemId", -1);
        int amount = Json.GetInt(body, "amount", 0);
        if (characterId <= 0 || itemId <= 0 || amount <= 0)
        {
            return "{\"error\":\"paramètres invalides\"}";
        }
        if (IsCharacterOnline(characterId))
        {
            return "{\"error\":\"ce joueur est en ligne : utilisez son inventaire en ligne\"}";
        }
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(),
            SQLite.SQLiteOpenFlags.ReadWrite, false);
        try
        {
            List<CharRow> rows = conn.Query<CharRow>("SELECT Id, Inventory FROM Characters WHERE Id = ?", characterId);
            if (rows.Count == 0 || string.IsNullOrEmpty(rows[0].Inventory))
            {
                return "{\"error\":\"personnage ou inventaire introuvable\"}";
            }
            string json = rows[0].Inventory;
            int remaining = amount;
            // retire slot par slot ; slot vidé -> itemId 0 (slot libre, format du jeu)
            string updated = Regex.Replace(json,
                "\\{\\s*\"itemId\"\\s*:\\s*" + itemId + "\\s*,\\s*\"number\"\\s*:\\s*(?<n>\\d+)\\s*,\\s*\"data\"\\s*:\\s*\"(?<d>(?:\\\\.|[^\"])*)\"\\s*\\}",
                delegate (Match m)
                {
                    if (remaining <= 0)
                    {
                        return m.Value;
                    }
                    int n = int.Parse(m.Groups["n"].Value);
                    int take = Math.Min(n, remaining);
                    remaining -= take;
                    int left = n - take;
                    if (left > 0)
                    {
                        return "{\"itemId\":" + itemId + ",\"number\":" + left + ",\"data\":\"" + m.Groups["d"].Value + "\"}";
                    }
                    return "{\"itemId\":0,\"number\":0,\"data\":\"\"}";
                });
            int removed = amount - remaining;
            if (removed <= 0)
            {
                return "{\"error\":\"le personnage n'a pas cet item\"}";
            }
            conn.Execute("UPDATE Characters SET Inventory = ? WHERE Id = ?", updated, characterId);
            Debug.Log("[TKWEB] OFFLINE-REMOVEITEM charId=" + characterId + " item=" + itemId + " x" + removed);
            StaffLog("retire (hors ligne) " + removed + "x item " + itemId + " du perso #" + characterId);
            return "{\"ok\":true,\"removed\":" + removed + "}";
        }
        finally
        {
            conn.Close();
        }
    }

    private string ApiOfflineVehicles(string characterIdStr)
    {
        int characterId;
        if (!int.TryParse(characterIdStr ?? "", out characterId) || characterId <= 0)
        {
            return "{\"error\":\"characterId invalide\"}";
        }
        return (string)RunOnMain(delegate
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            HashSet<int> seen = new HashSet<int>();
            foreach (LifeVehicle v in Nova.v.vehicles)
            {
                if (v == null || v.permissions == null || !seen.Add(v.vehicleId))
                {
                    continue; // null ou doublon en mémoire
                }
                bool owns = false;
                try { owns = v.permissions.HasPermission(characterId); } catch { }
                if (!owns)
                {
                    continue;
                }
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"vehicleId\":").Append(v.vehicleId);
                sb.Append(",\"modelId\":").Append(v.modelId);
                sb.Append(",\"name\":").Append(Json.Str(VehicleModelName(v.modelId)));
                sb.Append(",\"plate\":").Append(Json.Str(v.plate ?? ""));
                sb.Append(",\"isStowed\":").Append(v.isStowed ? "true" : "false");
                sb.Append(",\"fuel\":").Append(v.fuel.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        });
    }

    // ------------------------------------------------------------------
    // Fiche joueur, propriétés, entreprises (lecture life.db)
    // ------------------------------------------------------------------
    private class CardRow
    {
        public int Id { get; set; }
        public string Firstname { get; set; }
        public string Lastname { get; set; }
        public double Bank { get; set; }
        public string Inventory { get; set; }
        public int Health { get; set; }
        public int Hunger { get; set; }
        public int Thirst { get; set; }
        public bool PermisB { get; set; }
        public int PermisPoints { get; set; }
        public int WorkTime { get; set; }
        public int XP { get; set; }
        public int Level { get; set; }
        public string PhoneNumber { get; set; }
        public string Commune { get; set; }
        public int PrisonTime { get; set; }
        public double LastPosX { get; set; }
        public double LastPosY { get; set; }
        public double LastPosZ { get; set; }
        public long LastDisconnect { get; set; }
        public int StatDiamond { get; set; }
        public int StatCopper { get; set; }
        public int StatRock { get; set; }
        public int StatTree { get; set; }
        public int BizId { get; set; }
        public string Username { get; set; }
        public int AdminLevel { get; set; }
    }

    private class WarnRow
    {
        public string Text { get; set; }
        public string Admin { get; set; }
        public string Date { get; set; }
        public int Level { get; set; }
    }

    private class BizRow
    {
        public int Id { get; set; }
        public string BizName { get; set; }
        public string Activities { get; set; }
        public int OwnerId { get; set; }
        public int TerrainId { get; set; }
        public double Bank { get; set; }
        public double Salaire { get; set; }
        public bool IsRecruiting { get; set; }
    }

    private class AreaRow
    {
        public int AreaId { get; set; }
        public string Permissions { get; set; }
        public double Price { get; set; }
        public double RentPrice { get; set; }
        public bool IsRentable { get; set; }
    }

    private string ApiPlayerCard(string characterIdStr)
    {
        int characterId;
        if (!int.TryParse(characterIdStr ?? "", out characterId) || characterId <= 0)
        {
            return "{\"error\":\"characterId invalide\"}";
        }
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            List<CardRow> rows = conn.Query<CardRow>(
                "SELECT c.Id, c.Firstname, c.Lastname, c.Bank, c.Inventory, c.Health, c.Hunger, c.Thirst, " +
                "c.PermisB, c.PermisPoints, c.WorkTime, c.XP, c.Level, c.PhoneNumber, c.Commune, c.PrisonTime, " +
                "c.LastPosX, c.LastPosY, c.LastPosZ, c.LastDisconnect, " +
                "c.StatDiamond, c.StatCopper, c.StatRock, c.StatTree, c.BizId, " +
                "a.Username, a.AdminLevel " +
                "FROM Characters c LEFT JOIN Accounts a ON a.Id = c.AccountId WHERE c.Id = ?", characterId);
            if (rows.Count == 0)
            {
                return "{\"error\":\"personnage introuvable\"}";
            }
            CardRow r = rows[0];

            string bizName = "";
            if (r.BizId > 0)
            {
                List<BizRow> biz = conn.Query<BizRow>("SELECT Id, BizName, Activities, OwnerId, TerrainId, Bank, Salaire, IsRecruiting FROM Bizs WHERE Id = ?", r.BizId);
                if (biz.Count > 0)
                {
                    bizName = biz[0].BizName ?? "";
                }
            }

            int areaCount = 0;
            foreach (AreaRow a in conn.Query<AreaRow>("SELECT AreaId, Permissions, Price, RentPrice, IsRentable FROM Areas WHERE Permissions LIKE ?", "%\"characterId\":" + characterId + "%"))
            {
                areaCount++;
            }

            StringBuilder warns = new StringBuilder("[");
            bool wf = true;
            foreach (WarnRow w in conn.Query<WarnRow>("SELECT Text, Admin, Date, Level FROM Warns WHERE CharacterId = ? ORDER BY Id DESC LIMIT 20", characterId))
            {
                if (!wf) warns.Append(",");
                wf = false;
                warns.Append("{\"text\":").Append(Json.Str(w.Text ?? ""));
                warns.Append(",\"admin\":").Append(Json.Str(w.Admin ?? ""));
                warns.Append(",\"date\":").Append(Json.Str(w.Date ?? ""));
                warns.Append(",\"level\":").Append(w.Level).Append("}");
            }
            warns.Append("]");

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"characterId\":").Append(r.Id);
            sb.Append(",\"username\":").Append(Json.Str(r.Username ?? ""));
            sb.Append(",\"adminLevel\":").Append(r.AdminLevel);
            sb.Append(",\"firstname\":").Append(Json.Str(r.Firstname ?? ""));
            sb.Append(",\"lastname\":").Append(Json.Str(r.Lastname ?? ""));
            sb.Append(",\"money\":").Append(WalletMoney(r.Inventory).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"bank\":").Append(r.Bank.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"health\":").Append(r.Health);
            sb.Append(",\"hunger\":").Append(r.Hunger);
            sb.Append(",\"thirst\":").Append(r.Thirst);
            sb.Append(",\"permisB\":").Append(r.PermisB ? "true" : "false");
            sb.Append(",\"permisPoints\":").Append(r.PermisPoints);
            sb.Append(",\"level\":").Append(r.Level);
            sb.Append(",\"xp\":").Append(r.XP);
            sb.Append(",\"workTimeMin\":").Append(r.WorkTime);
            sb.Append(",\"prisonTime\":").Append(r.PrisonTime);
            sb.Append(",\"phone\":").Append(Json.Str(r.PhoneNumber ?? ""));
            sb.Append(",\"commune\":").Append(Json.Str(r.Commune ?? ""));
            sb.Append(",\"lastDisconnect\":").Append(r.LastDisconnect);
            sb.Append(",\"lastX\":").Append(r.LastPosX.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"lastY\":").Append(r.LastPosY.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"lastZ\":").Append(r.LastPosZ.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"statDiamond\":").Append(r.StatDiamond);
            sb.Append(",\"statCopper\":").Append(r.StatCopper);
            sb.Append(",\"statRock\":").Append(r.StatRock);
            sb.Append(",\"statTree\":").Append(r.StatTree);
            sb.Append(",\"bizId\":").Append(r.BizId);
            sb.Append(",\"bizName\":").Append(Json.Str(bizName));
            sb.Append(",\"areaCount\":").Append(areaCount);
            sb.Append(",\"warns\":").Append(warns);
            sb.Append("}");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    private string ApiPlayerAreas(string characterIdStr)
    {
        int characterId;
        if (!int.TryParse(characterIdStr ?? "", out characterId) || characterId <= 0)
        {
            return "{\"error\":\"characterId invalide\"}";
        }
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (AreaRow a in conn.Query<AreaRow>(
                "SELECT AreaId, Permissions, Price, RentPrice, IsRentable FROM Areas WHERE Permissions LIKE ?",
                "%\"characterId\":" + characterId + "%"))
            {
                // vérifie que c'est bien le propriétaire (owner), pas un co-owner homonyme partiel
                bool owner = Regex.IsMatch(a.Permissions ?? "",
                    "\"owner\"\\s*:\\s*\\{[^}]*\"characterId\"\\s*:\\s*" + characterId + "\\b");
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"areaId\":").Append(a.AreaId);
                sb.Append(",\"owner\":").Append(owner ? "true" : "false");
                sb.Append(",\"price\":").Append(a.Price.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"rentPrice\":").Append(a.RentPrice.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"isRentable\":").Append(a.IsRentable ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    private string ApiBizs()
    {
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (BizRow b in conn.Query<BizRow>(
                "SELECT Id, BizName, Activities, OwnerId, TerrainId, Bank, Salaire, IsRecruiting FROM Bizs ORDER BY Id"))
            {
                string ownerName = "";
                List<CharRow2> o = conn.Query<CharRow2>("SELECT Id, Firstname, Lastname FROM Characters WHERE Id = ?", b.OwnerId);
                if (o.Count > 0)
                {
                    ownerName = (o[0].Firstname + " " + o[0].Lastname).Trim();
                }
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":").Append(b.Id);
                sb.Append(",\"name\":").Append(Json.Str(b.BizName ?? ""));
                sb.Append(",\"activities\":").Append(Json.Str(b.Activities ?? ""));
                sb.Append(",\"ownerId\":").Append(b.OwnerId);
                sb.Append(",\"ownerName\":").Append(Json.Str(ownerName));
                sb.Append(",\"terrainId\":").Append(b.TerrainId);
                sb.Append(",\"bank\":").Append(b.Bank.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"salaire\":").Append(b.Salaire.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"recruiting\":").Append(b.IsRecruiting ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    private class CharRow2
    {
        public int Id { get; set; }
        public string Firstname { get; set; }
        public string Lastname { get; set; }
    }

    // ------------------------------------------------------------------
    // Optimisation FPS : état des véhicules fantômes + terrains lourds
    // ------------------------------------------------------------------
    private object ApiGhostStats()
    {
        int real = 0, ghosts = 0, stowed = 0;
        try
        {
            foreach (LifeVehicle v in Nova.v.vehicles)
            {
                if (v == null) continue;
                if (v.isStowed) stowed++;
                else if (v.fake != null) ghosts++;
                else if (v.instance != null) real++;
            }
        }
        catch
        {
        }
        long ghosted = 0;
        bool pluginLoaded = false;
        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType("TKGhost");
            if (t != null)
            {
                pluginLoaded = true;
                System.Reflection.FieldInfo f = t.GetField("ghostedSinceBoot");
                if (f != null)
                {
                    ghosted = Convert.ToInt64(f.GetValue(null));
                }
                break;
            }
        }
        return "{\"pluginLoaded\":" + (pluginLoaded ? "true" : "false")
            + ",\"real\":" + real + ",\"ghosts\":" + ghosts + ",\"stowed\":" + stowed
            + ",\"ghostedSinceBoot\":" + ghosted + "}";
    }

    // Fixe la limite d'objets d'un terrain (en mémoire + base, sans restart)
    private string ApiSetAreaLimit(string body)
    {
        int areaId = Json.GetInt(body, "areaId", -1);
        int maxObjects = Json.GetInt(body, "maxObjects", -2);
        if (areaId < 0 || maxObjects < -1 || maxObjects > 100000)
        {
            return "{\"error\":\"paramètres invalides (-1 = illimité)\"}";
        }
        return (string)RunOnMain(delegate
        {
            Life.AreaSystem.LifeArea area = Nova.a.GetAreaById((uint)areaId);
            if (area == null)
            {
                return "{\"error\":\"terrain introuvable\"}";
            }
            area.maxObjects = maxObjects;
            LifeDB.SaveArea(area);
            Debug.Log("[TKWEB] AREALIMIT terrain=" + areaId + " maxObjects=" + maxObjects);
            StaffLog("limite du terrain #" + areaId + " fixée à " + (maxObjects < 0 ? "illimité" : maxObjects.ToString()) + " objets");
            return "{\"ok\":true}";
        });
    }

    // Notifie le propriétaire du terrain (s'il est en ligne)
    private string ApiNotifyAreaOwner(string body)
    {
        int areaId = Json.GetInt(body, "areaId", -1);
        string text = Json.GetString(body, "text", "");
        if (areaId < 0)
        {
            return "{\"error\":\"areaId invalide\"}";
        }
        if (string.IsNullOrEmpty(text))
        {
            text = "Votre terrain #" + areaId + " contient trop d'objets et fait chuter les FPS : merci de l'alléger.";
        }
        string msg = text;
        return (string)RunOnMain(delegate
        {
            Life.AreaSystem.LifeArea area = Nova.a.GetAreaById((uint)areaId);
            if (area == null)
            {
                return "{\"error\":\"terrain introuvable\"}";
            }
            int ownerId = -1;
            try
            {
                if (area.permissions != null && area.permissions.owner != null)
                {
                    ownerId = area.permissions.owner.characterId;
                }
            }
            catch
            {
            }
            if (ownerId <= 0)
            {
                return "{\"error\":\"ce terrain n'a pas de propriétaire joueur\"}";
            }
            foreach (Player p in Nova.server.GetAllInGamePlayers())
            {
                try
                {
                    if (p != null && p.character != null && p.character.Id == ownerId)
                    {
                        p.Notify("Terrain #" + areaId, msg);
                        p.SendText("<color=#ffb454>[STAFF]</color> " + msg);
                        StaffLog("notifie le proprio du terrain #" + areaId + " (" + PseudoOf(p) + ") : " + msg);
                        return "{\"ok\":true,\"pseudo\":" + Json.Str(PseudoOf(p)) + "}";
                    }
                }
                catch
                {
                }
            }
            return "{\"error\":\"le propriétaire (perso #" + ownerId + ") n'est pas en ligne\"}";
        });
    }

    // Dossier de sauvegardes DANS l'instance (visible conteneur + hôte)
    private static string BackupDir()
    {
        return Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "backups"));
    }

    private const int BackupKeep = 28; // ~7 jours à 1 backup/6 h + manuels

    // Sauvegarde à chaud via SQLite VACUUM INTO (cohérent en écriture),
    // repli sur checkpoint WAL + copie de fichier si VACUUM INTO indispo.
    private static string DoBackup(out string fileName)
    {
        fileName = null;
        string dir = BackupDir();
        Directory.CreateDirectory(dir);
        string db = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "life.db"));
        if (!File.Exists(db))
        {
            return "life.db introuvable";
        }
        string name = "life-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".db";
        string target = Path.Combine(dir, name);
        if (File.Exists(target))
        {
            name = "life-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".db";
            target = Path.Combine(dir, name);
        }
        try
        {
            SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(db, SQLite.SQLiteOpenFlags.ReadWrite, false);
            try
            {
                string safe = target.Replace("'", "''");
                conn.Execute("VACUUM INTO '" + safe + "'");
            }
            finally
            {
                conn.Close();
            }
        }
        catch (Exception ex)
        {
            // repli : checkpoint puis copie brute
            try
            {
                SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(db, SQLite.SQLiteOpenFlags.ReadWrite, false);
                try { conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
                conn.Close();
                File.Copy(db, target, true);
                Debug.LogWarning("[TKWEB] Backup via copie (VACUUM INTO indispo : " + ex.Message + ")");
            }
            catch (Exception ex2)
            {
                return "échec sauvegarde : " + ex2.Message;
            }
        }
        // rotation
        try
        {
            List<string> files = new List<string>(Directory.GetFiles(dir, "life-*.db"));
            files.Sort();
            while (files.Count > BackupKeep)
            {
                File.Delete(files[0]);
                files.RemoveAt(0);
            }
        }
        catch
        {
        }
        fileName = name;
        return null;
    }

    private string ApiBackups()
    {
        StringBuilder sb = new StringBuilder("{\"dir\":");
        string dir = BackupDir();
        sb.Append(Json.Str(dir)).Append(",\"backups\":[");
        try
        {
            if (Directory.Exists(dir))
            {
                List<string> files = new List<string>(Directory.GetFiles(dir, "life-*.db"));
                files.Sort();
                files.Reverse();
                bool first = true;
                foreach (string f in files)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    FileInfo fi = new FileInfo(f);
                    sb.Append("{\"name\":").Append(Json.Str(fi.Name));
                    sb.Append(",\"sizeKb\":").Append(fi.Length / 1024L);
                    sb.Append(",\"date\":").Append(Json.Str(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                    sb.Append("}");
                }
            }
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private string ApiBackupNow()
    {
        string fileName;
        string err = DoBackup(out fileName);
        if (err != null)
        {
            return "{\"error\":" + Json.Str(err) + "}";
        }
        StaffLog("sauvegarde manuelle de la base : " + fileName);
        Debug.Log("[TKWEB] BACKUP manuel -> " + fileName);
        return "{\"ok\":true,\"name\":" + Json.Str(fileName) + "}";
    }

    // Sauvegarde automatique périodique (thread de fond)
    private void StartAutoBackup()
    {
        Thread t = new Thread(delegate ()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(config.backupIntervalHours * 3600 * 1000);
                    string fn;
                    string err = DoBackup(out fn);
                    if (err == null)
                    {
                        Debug.Log("[TKWEB] Sauvegarde auto -> " + fn);
                    }
                    else
                    {
                        Debug.LogError("[TKWEB] Sauvegarde auto échouée : " + err);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TKWEB] Erreur boucle sauvegarde : " + ex.Message);
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // Liste des plugins (.dll) du serveur : TeamKit + autres
    private string ApiPlugins()
    {
        string dir = Path.GetDirectoryName(pluginDir); // .../Plugins
        StringBuilder tk = new StringBuilder("[");
        StringBuilder other = new StringBuilder("[");
        bool ftk = true, fo = true;
        try
        {
            List<string> files = new List<string>(Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly));
            files.Sort();
            foreach (string f in files)
            {
                string fname = Path.GetFileName(f);
                bool isTk = fname.StartsWith("TK", StringComparison.OrdinalIgnoreCase);
                FileInfo fi = new FileInfo(f);
                string entry = "{\"name\":" + Json.Str(fname)
                    + ",\"sizeKb\":" + (fi.Length / 1024L)
                    + ",\"date\":" + Json.Str(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")) + "}";
                if (isTk)
                {
                    if (!ftk) tk.Append(",");
                    ftk = false;
                    tk.Append(entry);
                }
                else
                {
                    if (!fo) other.Append(",");
                    fo = false;
                    other.Append(entry);
                }
            }
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
        tk.Append("]");
        other.Append("]");
        return "{\"teamkit\":" + tk + ",\"others\":" + other + "}";
    }

    private class HeavyAreaRow
    {
        public int AreaId { get; set; }
        public int N { get; set; }
    }

    // Terrains avec le plus d'objets posés = principaux tueurs de FPS client
    private string ApiHeavyAreas()
    {
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            List<HeavyAreaRow> rows = conn.Query<HeavyAreaRow>(
                "SELECT AreaId AS AreaId, COUNT(*) AS N FROM Objects GROUP BY AreaId ORDER BY N DESC LIMIT 30");
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (HeavyAreaRow r in rows)
            {
                string ownerName = "";
                int maxObjects = 0;
                List<AreaRow> areas = conn.Query<AreaRow>("SELECT AreaId, Permissions, Price, RentPrice, IsRentable FROM Areas WHERE AreaId = ?", r.AreaId);
                if (areas.Count > 0)
                {
                    Match m = Regex.Match(areas[0].Permissions ?? "", "\"owner\"\\s*:\\s*\\{[^}]*\"characterId\"\\s*:\\s*(?<c>\\d+)");
                    if (m.Success)
                    {
                        int charId = int.Parse(m.Groups["c"].Value);
                        List<CharRow2> o = conn.Query<CharRow2>("SELECT Id, Firstname, Lastname FROM Characters WHERE Id = ?", charId);
                        if (o.Count > 0)
                        {
                            ownerName = (o[0].Firstname + " " + o[0].Lastname).Trim();
                        }
                    }
                    List<MaxObjRow> mo = conn.Query<MaxObjRow>("SELECT MaxObjects FROM Areas WHERE AreaId = ?", r.AreaId);
                    if (mo.Count > 0)
                    {
                        maxObjects = mo[0].MaxObjects;
                    }
                }
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"areaId\":").Append(r.AreaId);
                sb.Append(",\"objects\":").Append(r.N);
                sb.Append(",\"maxObjects\":").Append(maxObjects);
                sb.Append(",\"ownerName\":").Append(Json.Str(ownerName));
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    private class MaxObjRow
    {
        public int MaxObjects { get; set; }
    }

    // ------------------------------------------------------------------
    // Chat (v2.0)
    // ------------------------------------------------------------------
    private string ApiChat(string afterStr)
    {
        long after = 0;
        long.TryParse(afterStr ?? "0", out after);
        StringBuilder sb = new StringBuilder();
        lock (chatLock)
        {
            sb.Append("{\"last\":").Append(chatLastId).Append(",\"messages\":[");
            bool first = true;
            foreach (string entry in chatRing)
            {
                Match m = Regex.Match(entry, "\"id\":(?<i>\\d+)");
                if (m.Success && long.Parse(m.Groups["i"].Value) <= after)
                {
                    continue;
                }
                if (!first) sb.Append(",");
                first = false;
                sb.Append(entry);
            }
            sb.Append("]}");
        }
        return sb.ToString();
    }

    private string ApiChatSend(string body)
    {
        string text = Json.GetString(body, "text", "");
        if (string.IsNullOrEmpty(text))
        {
            return "{\"error\":\"message vide\"}";
        }
        return (string)RunOnMain(delegate
        {
            if (Nova.server == null)
            {
                return "{\"error\":\"serveur indisponible\"}";
            }
            Nova.server.SendMessageToAll("<color=#ff8800>[STAFF]</color> <color=#ffffff>" + text + "</color>");
            RecordChat("[PANEL STAFF]", "", text);
            return "{\"ok\":true}";
        });
    }

    private string ApiChatHistory(string date)
    {
        if (string.IsNullOrEmpty(date) || !Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
        {
            // liste des jours disponibles
            StringBuilder days = new StringBuilder("[");
            try
            {
                bool first = true;
                List<string> files = new List<string>(Directory.GetFiles(chatDir, "chat-*.log"));
                files.Sort();
                files.Reverse();
                foreach (string f in files)
                {
                    Match m = Regex.Match(Path.GetFileName(f), @"^chat-(\d{4}-\d{2}-\d{2})\.log$");
                    if (!m.Success) continue;
                    if (!first) days.Append(",");
                    first = false;
                    days.Append(Json.Str(m.Groups[1].Value));
                }
            }
            catch
            {
            }
            days.Append("]");
            return "{\"days\":" + days + "}";
        }
        try
        {
            string file = Path.Combine(chatDir, "chat-" + date + ".log");
            if (!File.Exists(file))
            {
                return "{\"error\":\"aucun chat ce jour-là\"}";
            }
            string[] lines = File.ReadAllLines(file);
            StringBuilder sb = new StringBuilder("{\"lines\":[");
            int start = Math.Max(0, lines.Length - 1000);
            for (int i = start; i < lines.Length; i++)
            {
                if (i > start) sb.Append(",");
                sb.Append(Json.Str(lines[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    // ------------------------------------------------------------------
    // Gestion des admins (v2.0)
    // ------------------------------------------------------------------
    private class AdminRow
    {
        public string SteamId { get; set; }
        public string Username { get; set; }
        public int AdminLevel { get; set; }
    }

    private string ApiAdmins()
    {
        HashSet<string> wl = AcWhitelist();
        HashSet<string> online = new HashSet<string>();
        try
        {
            object result = RunOnMain(delegate
            {
                HashSet<string> ids = new HashSet<string>();
                foreach (Player p in Nova.server.GetAllPlayers())
                {
                    if (p != null) ids.Add(p.steamId.ToString());
                }
                return ids;
            });
            online = (HashSet<string>)result;
        }
        catch
        {
        }
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (AdminRow a in conn.Query<AdminRow>(
                "SELECT SteamId, Username, AdminLevel FROM Accounts WHERE AdminLevel > 0 ORDER BY AdminLevel DESC, Username"))
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"steamId\":").Append(Json.Str(a.SteamId ?? ""));
                sb.Append(",\"username\":").Append(Json.Str(a.Username ?? ""));
                sb.Append(",\"level\":").Append(a.AdminLevel);
                sb.Append(",\"online\":").Append(online.Contains(a.SteamId ?? "") ? "true" : "false");
                sb.Append(",\"whitelisted\":").Append(wl.Contains(a.SteamId ?? "") ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    // Garde l'anti-cheat en phase : un admin promu par le panel est légitime
    // Whitelist anti-cheat actuelle (lue depuis TKAntiCheat/config.json)
    private HashSet<string> AcWhitelist()
    {
        HashSet<string> ids = new HashSet<string>();
        try
        {
            string cfg = AcConfigPath();
            if (File.Exists(cfg))
            {
                Match m = Regex.Match(File.ReadAllText(cfg), "\"adminWhitelist\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
                if (m.Success)
                {
                    foreach (string x in m.Groups["v"].Value.Split(','))
                    {
                        string t = x.Trim();
                        if (t.Length > 0)
                        {
                            ids.Add(t);
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return ids;
    }

    // Ajout/retrait manuel en liste blanche depuis la gestion des admins (owner)
    private string ApiAcWl(string body)
    {
        string steamId = Json.GetString(body, "steamId", "").Trim();
        bool add = Json.GetInt(body, "add", 1) == 1;
        if (!Regex.IsMatch(steamId, "^[0-9]{17}$"))
        {
            return "{\"error\":\"SteamID64 invalide\"}";
        }
        SyncAntiCheatWhitelist(steamId, add);
        StaffLog((add ? "ajoute " : "retire ") + steamId + (add ? " a" : " de") + " la liste blanche anti-cheat");
        Debug.Log("[TKWEB] ACWL " + (add ? "+" : "-") + steamId);
        return "{\"ok\":true}";
    }

    private static void SyncAntiCheatWhitelist(string steamId, bool add)
    {
        try
        {
            string cfg = Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), "TKAntiCheat"), "config.json");
            if (!File.Exists(cfg))
            {
                return;
            }
            string json = File.ReadAllText(cfg);
            Match m = Regex.Match(json, "\"adminWhitelist\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
            if (!m.Success)
            {
                return;
            }
            List<string> ids = new List<string>(m.Groups["v"].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            for (int i = 0; i < ids.Count; i++) ids[i] = ids[i].Trim();
            bool changed = false;
            if (add && !ids.Contains(steamId))
            {
                ids.Add(steamId);
                changed = true;
            }
            else if (!add && ids.Contains(steamId))
            {
                ids.Remove(steamId);
                changed = true;
            }
            if (changed)
            {
                string updated = json.Substring(0, m.Groups["v"].Index)
                    + string.Join(",", ids.ToArray())
                    + json.Substring(m.Groups["v"].Index + m.Groups["v"].Length);
                File.WriteAllText(cfg, updated);
            }
        }
        catch
        {
        }
    }

    private string ApiSetAdmin(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int level = Json.GetInt(body, "level", -1);
        string pin = Json.GetString(body, "pin", "");
        if (string.IsNullOrEmpty(steamId) || level < 0 || level > 9)
        {
            return "{\"error\":\"steamId ou niveau invalide (0-9)\"}";
        }
        // joueur en ligne : applique en direct
        string onlineResult = (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.account == null)
            {
                return null;
            }
            p.account.adminLevel = level;
            if (!string.IsNullOrEmpty(pin))
            {
                p.account.adminPin = pin;
            }
            LifeDB.SaveAccount(p.account);
            try
            {
                p.Notify("Administration", level > 0 ? ("Niveau admin " + level + " attribué") : "Droits admin retirés");
            }
            catch
            {
            }
            Debug.Log("[TKWEB] SETADMIN steamid=" + steamId + " niveau=" + level + " (en ligne)");
            SyncAntiCheatWhitelist(steamId, level > 0);
            StaffLog("niveau admin " + level + " pour " + steamId);
            return "{\"ok\":true,\"online\":true}";
        });
        if (onlineResult != null)
        {
            return onlineResult;
        }
        Account account = LifeDB.FetchAccount(steamId).Result;
        if (account == null)
        {
            return "{\"error\":\"aucun compte avec ce SteamID\"}";
        }
        account.adminLevel = level;
        if (!string.IsNullOrEmpty(pin))
        {
            account.adminPin = pin;
        }
        bool saved = LifeDB.SaveAccount(account).Result;
        Debug.Log("[TKWEB] SETADMIN steamid=" + steamId + " niveau=" + level + " (hors ligne)");
        SyncAntiCheatWhitelist(steamId, level > 0);
        StaffLog("niveau admin " + level + " pour " + steamId + " (hors ligne)");
        return saved ? "{\"ok\":true,\"online\":false}" : "{\"error\":\"échec sauvegarde\"}";
    }

    // ------------------------------------------------------------------
    // Actions bonus (v2.0) : notification, prison, XP
    // ------------------------------------------------------------------
    private string ApiNotify(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string title = Json.GetString(body, "title", "Administration");
        string text = Json.GetString(body, "text", "");
        if (string.IsNullOrEmpty(text))
        {
            return "{\"error\":\"texte vide\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            p.Notify(title, text);
            return "{\"ok\":true}";
        });
    }

    private string ApiPrison(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int minutes = Json.GetInt(body, "minutes", -1);
        if (minutes < 0)
        {
            return "{\"error\":\"durée invalide (0 = libérer)\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            p.SetPrisonTime(minutes);
            p.Notify("Justice", minutes > 0 ? ("Vous êtes emprisonné " + minutes + " minutes") : "Vous êtes libéré");
            Debug.Log("[TKWEB] PRISON steamid=" + steamId + " minutes=" + minutes);
            StaffLog(minutes > 0 ? ("emprisonne " + steamId + " " + minutes + " min") : ("libere " + steamId));
            return "{\"ok\":true}";
        });
    }

    private string ApiGiveXp(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int amount = Json.GetInt(body, "amount", 0);
        if (amount <= 0)
        {
            return "{\"error\":\"quantité invalide\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            p.GiveXP(amount);
            p.Notify("Expérience", "+" + amount + " XP (staff)");
            Debug.Log("[TKWEB] GIVEXP steamid=" + steamId + " +" + amount);
            StaffLog("donne " + amount + " XP a " + steamId);
            return "{\"ok\":true}";
        });
    }

    // ------------------------------------------------------------------
    // Journal d'activité + messages ciblés + permis (v2.2)
    // ------------------------------------------------------------------
    private string ApiActivity(string afterStr, string kind)
    {
        long after = 0;
        long.TryParse(afterStr ?? "0", out after);
        StringBuilder sb = new StringBuilder();
        lock (actLock)
        {
            sb.Append("{\"last\":").Append(actLastId).Append(",\"messages\":[");
            bool first = true;
            foreach (string entry in actRing)
            {
                Match m = Regex.Match(entry, "\"id\":(?<i>\\d+)");
                if (m.Success && long.Parse(m.Groups["i"].Value) <= after)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(kind) && !entry.Contains("\"kind\":\"" + kind + "\""))
                {
                    continue;
                }
                if (!first) sb.Append(",");
                first = false;
                sb.Append(entry);
            }
            sb.Append("]}");
        }
        return sb.ToString();
    }

    private string ApiActivityHistory(string date)
    {
        if (string.IsNullOrEmpty(date) || !Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$"))
        {
            StringBuilder days = new StringBuilder("[");
            try
            {
                bool first = true;
                List<string> files = new List<string>(Directory.GetFiles(actDir, "activity-*.log"));
                files.Sort();
                files.Reverse();
                foreach (string f in files)
                {
                    Match m = Regex.Match(Path.GetFileName(f), @"^activity-(\d{4}-\d{2}-\d{2})\.log$");
                    if (!m.Success) continue;
                    if (!first) days.Append(",");
                    first = false;
                    days.Append(Json.Str(m.Groups[1].Value));
                }
            }
            catch
            {
            }
            days.Append("]");
            return "{\"days\":" + days + "}";
        }
        try
        {
            string file = Path.Combine(actDir, "activity-" + date + ".log");
            if (!File.Exists(file))
            {
                return "{\"error\":\"aucune activité ce jour-là\"}";
            }
            string[] lines = File.ReadAllLines(file);
            StringBuilder sb = new StringBuilder("{\"lines\":[");
            int start = Math.Max(0, lines.Length - 1500);
            for (int i = start; i < lines.Length; i++)
            {
                if (i > start) sb.Append(",");
                sb.Append(Json.Str(lines[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    private string ApiMsgAdmins(string body)
    {
        string text = Json.GetString(body, "text", "");
        if (string.IsNullOrEmpty(text))
        {
            return "{\"error\":\"message vide\"}";
        }
        return (string)RunOnMain(delegate
        {
            if (Nova.server == null)
            {
                return "{\"error\":\"serveur indisponible\"}";
            }
            Nova.server.SendMessageToAdmins("<color=#ffb454>[STAFF→ADMINS]</color> " + text);
            StaffLog("message aux admins : " + text);
            return "{\"ok\":true}";
        });
    }

    private string ApiLocalMsg(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        string text = Json.GetString(body, "text", "");
        double range = Json.GetDouble(body, "range", 60);
        if (string.IsNullOrEmpty(text))
        {
            return "{\"error\":\"message vide\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null)
            {
                return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
            }
            Vector3 pos = p.setup.transform.position;
            Nova.server.SendLocalText("<color=#00f0ff>[LOCAL]</color> " + text, (float)range, pos);
            StaffLog("message local (" + (int)range + " m autour de " + PseudoOf(p) + ") : " + text);
            return "{\"ok\":true}";
        });
    }

    private string ApiPermis(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int points = Json.GetInt(body, "points", -1);
        if (points < 0 || points > 12)
        {
            return "{\"error\":\"points invalides (0-12)\"}";
        }
        return (string)RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.character == null)
            {
                return "{\"error\":\"joueur introuvable ou personnage non chargé\"}";
            }
            p.character.PermisPoints = points;
            LifeDB.SaveCharacter(p.character);
            p.Notify("Permis", "Vos points de permis : " + points + "/12");
            StaffLog("points de permis de " + PseudoOf(p) + " fixes a " + points);
            Debug.Log("[TKWEB] PERMIS steamid=" + steamId + " points=" + points);
            return "{\"ok\":true}";
        });
    }

    // ------------------------------------------------------------------
    // SMS / Contacts / Mails (v2.3, admin+)
    // ------------------------------------------------------------------
    private class SmsRow
    {
        public string NumberEmitter { get; set; }
        public string NumberReceiver { get; set; }
        public long Timestamp { get; set; }
        public string Message { get; set; }
    }

    private class ContactRow
    {
        public string Name { get; set; }
        public string Number { get; set; }
    }

    private class MailRow
    {
        public string Recipient { get; set; }
        public string Sender { get; set; }
        public string Subject { get; set; }
        public string Content { get; set; }
        public long Timestamp { get; set; }
    }

    private string ApiSms(string characterIdStr)
    {
        int characterId;
        if (!int.TryParse(characterIdStr ?? "", out characterId) || characterId <= 0)
        {
            return "{\"error\":\"characterId invalide\"}";
        }
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (SmsRow r in conn.Query<SmsRow>(
                "SELECT NumberEmitter, NumberReceiver, Timestamp, Message FROM SMS WHERE CharacterId = ? ORDER BY Id DESC LIMIT 200", characterId))
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"from\":").Append(Json.Str(r.NumberEmitter ?? ""));
                sb.Append(",\"to\":").Append(Json.Str(r.NumberReceiver ?? ""));
                sb.Append(",\"timestamp\":").Append(r.Timestamp);
                sb.Append(",\"text\":").Append(Json.Str(r.Message ?? ""));
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    private string ApiContacts(string characterIdStr)
    {
        int characterId;
        if (!int.TryParse(characterIdStr ?? "", out characterId) || characterId <= 0)
        {
            return "{\"error\":\"characterId invalide\"}";
        }
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (ContactRow r in conn.Query<ContactRow>(
                "SELECT Name, Number FROM Contacts WHERE CharacterId = ? ORDER BY Name LIMIT 200", characterId))
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":").Append(Json.Str(r.Name ?? ""));
                sb.Append(",\"number\":").Append(Json.Str(r.Number ?? "")).Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    private string ApiMails()
    {
        SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
        try
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (MailRow r in conn.Query<MailRow>(
                "SELECT Recipient, Sender, Subject, Content, Timestamp FROM Mails ORDER BY Id DESC LIMIT 100"))
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"from\":").Append(Json.Str(r.Sender ?? ""));
                sb.Append(",\"to\":").Append(Json.Str(r.Recipient ?? ""));
                sb.Append(",\"subject\":").Append(Json.Str(r.Subject ?? ""));
                sb.Append(",\"content\":").Append(Json.Str(r.Content ?? ""));
                sb.Append(",\"timestamp\":").Append(r.Timestamp).Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }
        finally
        {
            conn.Close();
        }
    }

    // ------------------------------------------------------------------
    // Comptes panel (v2.3, owner)
    // ------------------------------------------------------------------
    private string ApiPanelUsers()
    {
        StringBuilder sb = new StringBuilder("[");
        lock (usersLock)
        {
            for (int i = 0; i < panelUsers.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":").Append(Json.Str(panelUsers[i].name));
                sb.Append(",\"level\":").Append(panelUsers[i].level).Append("}");
            }
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string ApiPanelUserSet(string body)
    {
        string name = Json.GetString(body, "name", "").Trim();
        string password = Json.GetString(body, "password", "");
        int level = Json.GetInt(body, "level", 0);
        if (name.Length == 0 || name.Length > 30 || level < 1 || level > 2)
        {
            return "{\"error\":\"nom ou niveau invalide (1=modo, 2=admin)\"}";
        }
        if (string.Equals(name, "owner", StringComparison.OrdinalIgnoreCase))
        {
            return "{\"error\":\"'owner' est réservé (mot de passe dans config.json)\"}";
        }
        lock (usersLock)
        {
            PanelUser existing = panelUsers.Find(delegate (PanelUser u) { return string.Equals(u.name, name, StringComparison.OrdinalIgnoreCase); });
            if (existing == null)
            {
                if (string.IsNullOrEmpty(password) || password.Length < 8)
                {
                    return "{\"error\":\"mot de passe requis (8 caractères minimum)\"}";
                }
                panelUsers.Add(new PanelUser { name = name, password = password, level = level });
            }
            else
            {
                existing.level = level;
                if (!string.IsNullOrEmpty(password))
                {
                    if (password.Length < 8)
                    {
                        return "{\"error\":\"mot de passe trop court (8 caractères minimum)\"}";
                    }
                    existing.password = password;
                }
            }
        }
        SavePanelUsers();
        Debug.Log("[TKWEB] PANELUSER set nom=" + name + " niveau=" + level);
        return "{\"ok\":true}";
    }

    private string ApiPanelUserDel(string body)
    {
        string name = Json.GetString(body, "name", "").Trim();
        int removed;
        lock (usersLock)
        {
            removed = panelUsers.RemoveAll(delegate (PanelUser u) { return string.Equals(u.name, name, StringComparison.OrdinalIgnoreCase); });
        }
        if (removed == 0)
        {
            return "{\"error\":\"compte introuvable\"}";
        }
        SavePanelUsers();
        Debug.Log("[TKWEB] PANELUSER suppression nom=" + name);
        return "{\"ok\":true}";
    }

    private string AcConfigPath()
    {
        return Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), "TKAntiCheat"), "config.json");
    }

    private string ApiAcConfig()
    {
        try
        {
            string f = AcConfigPath();
            if (!File.Exists(f))
            {
                return "{\"error\":\"TKAntiCheat non installé\"}";
            }
            string json = File.ReadAllText(f);
            string wl = "";
            Match m = Regex.Match(json, "\"adminWhitelist\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
            if (m.Success) wl = m.Groups["v"].Value;
            int wlCount = 0;
            foreach (string x in wl.Split(','))
            {
                if (x.Trim().Length > 0) wlCount++;
            }
            StringBuilder sb = new StringBuilder("{");
            sb.Append("\"adminProtection\":").Append(Json.GetBool(json, "adminProtection", true) ? "true" : "false");
            sb.Append(",\"adminAutoReset\":").Append(Json.GetBool(json, "adminAutoReset", false) ? "true" : "false");
            sb.Append(",\"adminKick\":").Append(Json.GetBool(json, "adminKick", false) ? "true" : "false");
            sb.Append(",\"spoofCheck\":").Append(Json.GetBool(json, "spoofCheck", true) ? "true" : "false");
            sb.Append(",\"adminIpGuard\":").Append(Json.GetBool(json, "adminIpGuard", false) ? "true" : "false");
            sb.Append(",\"adminIpKick\":").Append(Json.GetBool(json, "adminIpKick", false) ? "true" : "false");
            sb.Append(",\"spamEnabled\":").Append(Json.GetBool(json, "spamEnabled", true) ? "true" : "false");
            sb.Append(",\"spamKick\":").Append(Json.GetBool(json, "spamKick", true) ? "true" : "false");
            sb.Append(",\"enabled\":").Append(Json.GetBool(json, "enabled", true) ? "true" : "false");
            sb.Append(",\"moneyThreshold\":").Append(Json.GetInt(json, "moneyAlertThreshold", 500000));
            sb.Append(",\"maxSpeed\":").Append(Json.GetInt(json, "maxSpeed", 30));
            sb.Append(",\"whitelistCount\":").Append(wlCount);
            sb.Append(",\"whitelist\":").Append(Json.Str(wl));
            sb.Append("}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    private string ApiAcSet(string body)
    {
        string key = Json.GetString(body, "key", "");
        bool value = Json.GetBool(body, "value", false);
        string[] allowed = { "adminProtection", "adminAutoReset", "adminKick", "spamEnabled", "spamKick", "enabled", "spoofCheck", "spoofKick", "adminIpGuard", "adminIpKick" };
        if (Array.IndexOf(allowed, key) < 0)
        {
            return "{\"error\":\"réglage inconnu\"}";
        }
        try
        {
            string f = AcConfigPath();
            if (!File.Exists(f))
            {
                return "{\"error\":\"TKAntiCheat non installé\"}";
            }
            string json = File.ReadAllText(f);
            string repl = "\"" + key + "\": " + (value ? "true" : "false");
            string updated = Regex.Replace(json, "\"" + key + "\"\\s*:\\s*(true|false)", repl);
            if (updated == json && !Regex.IsMatch(json, "\"" + key + "\"\\s*:"))
            {
                return "{\"error\":\"clé absente du fichier\"}";
            }
            File.WriteAllText(f, updated);
            StaffLog("réglage anti-cheat " + key + " = " + (value ? "activé" : "désactivé"));
            Debug.Log("[TKWEB] ACSET " + key + "=" + value + " (pris en compte sous ~20 s)");
            return "{\"ok\":true}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    // --- Réglages génériques des plugins TeamKit (owner uniquement) ---
    // Clés éditables par plugin, au format "clé:type" (b = booléen, n = nombre, s = texte).
    private static readonly Dictionary<string, string[]> plugEditable = new Dictionary<string, string[]>
    {
        { "TKGhost", new string[] { "enabled:b", "ghostAfterMinutes:n", "playerRadiusMeters:n", "checkIntervalSeconds:n" } },
        { "TKDynamicFps", new string[] { "enabled:b", "idleFps:n", "minPlayersFps:n", "maxFps:n", "cpuLowPercent:n", "cpuHighPercent:n" } },
        { "TKAntiFlood", new string[] { "enabled:b", "maxAttempts:n", "windowSeconds:n", "banMinutes:n", "packetGuard:b", "packetThreshold:n", "whitelist:s" } },
        { "TKAntiCheat", new string[] { "moneyAlertThreshold:n", "maxSpeed:n", "spamThreshold:n", "spamWindowSeconds:n", "adminGraceSeconds:n", "adminWhitelist:s" } }
    };

    private string PlugConfigFile(string name)
    {
        return Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), name), "config.json");
    }

    private string ApiPlugConfig(string name)
    {
        if (name == null || !plugEditable.ContainsKey(name))
        {
            return "{\"error\":\"plugin inconnu\"}";
        }
        try
        {
            string f = PlugConfigFile(name);
            if (!File.Exists(f))
            {
                return "{\"error\":\"config introuvable (plugin jamais démarré ?)\"}";
            }
            string json = File.ReadAllText(f);
            StringBuilder sb = new StringBuilder("{\"plugin\":" + Json.Str(name) + ",\"values\":{");
            bool first = true;
            foreach (string def in plugEditable[name])
            {
                string[] kv = def.Split(':');
                string key = kv[0];
                string type = kv[1];
                if (!Regex.IsMatch(json, "\"" + key + "\"\\s*:"))
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(",");
                }
                first = false;
                sb.Append(Json.Str(key)).Append(":");
                if (type == "b")
                {
                    sb.Append(Json.GetBool(json, key, false) ? "true" : "false");
                }
                else if (type == "n")
                {
                    sb.Append(Json.GetDouble(json, key, 0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(Json.Str(Json.GetString(json, key, "")));
                }
            }
            sb.Append("}}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    private string ApiPlugSet(string body)
    {
        string plugin = Json.GetString(body, "plugin", "");
        string key = Json.GetString(body, "key", "");
        string value = Json.GetString(body, "value", "");
        if (!plugEditable.ContainsKey(plugin))
        {
            return "{\"error\":\"plugin inconnu\"}";
        }
        string type = null;
        foreach (string def in plugEditable[plugin])
        {
            string[] kv = def.Split(':');
            if (kv[0] == key)
            {
                type = kv[1];
                break;
            }
        }
        if (type == null)
        {
            return "{\"error\":\"réglage inconnu\"}";
        }
        try
        {
            string f = PlugConfigFile(plugin);
            if (!File.Exists(f))
            {
                return "{\"error\":\"config introuvable\"}";
            }
            string json = File.ReadAllText(f);
            string updated;
            if (type == "b")
            {
                bool b = value == "1" || value.ToLowerInvariant() == "true";
                updated = Regex.Replace(json, "\"" + key + "\"\\s*:\\s*(true|false)",
                    "\"" + key + "\": " + (b ? "true" : "false"));
            }
            else if (type == "n")
            {
                double d;
                if (!double.TryParse(value.Replace(",", "."), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d) || d < 0 || d > 100000000)
                {
                    return "{\"error\":\"valeur numérique invalide\"}";
                }
                string num = d == Math.Floor(d)
                    ? ((long)d).ToString()
                    : d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                updated = Regex.Replace(json, "\"" + key + "\"\\s*:\\s*-?[0-9][0-9.]*",
                    "\"" + key + "\": " + num);
            }
            else
            {
                // texte : uniquement pour des listes d'IP — caractères sûrs seulement
                string clean = Regex.Replace(value, "[^0-9a-fA-F.:, ]", "");
                if (plugin == "TKAntiCheat" && key == "adminWhitelist" && clean.Trim(new char[] { ' ', ',' }).Length == 0)
                {
                    return "{\"error\":\"liste blanche vide refusée (protection anti-effacement)\"}";
                }
                updated = Regex.Replace(json, "\"" + key + "\"\\s*:\\s*\"(?:\\\\.|[^\"])*\"",
                    "\"" + key + "\": \"" + clean + "\"");
            }
            if (updated == json && !Regex.IsMatch(json, "\"" + key + "\"\\s*:"))
            {
                return "{\"error\":\"clé absente du fichier\"}";
            }
            File.WriteAllText(f, updated);
            StaffLog("réglage " + plugin + " : " + key + " = " + value);
            Debug.Log("[TKWEB] PLUGSET " + plugin + "." + key + "=" + value + " (pris en compte sous ~30 s)");
            return "{\"ok\":true}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    // --- Test de charge FPS : véhicules de test spawnés en masse (owner) ---
    private string BenchFile()
    {
        return Path.Combine(pluginDir, "bench.json");
    }

    private List<int> LoadBenchIds()
    {
        List<int> ids = new List<int>();
        try
        {
            if (File.Exists(BenchFile()))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(BenchFile()), "[0-9]+"))
                {
                    ids.Add(int.Parse(m.Value));
                }
            }
        }
        catch
        {
        }
        return ids;
    }

    private void SaveBenchIds(List<int> ids)
    {
        try
        {
            File.WriteAllText(BenchFile(), "[" + string.Join(",", ids.ConvertAll(delegate (int i) { return i.ToString(); }).ToArray()) + "]");
        }
        catch
        {
        }
    }

    private string ApiBenchSpawn(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        int count = Json.GetInt(body, "count", 20);
        if (count < 1) count = 1;
        if (count > 40) count = 40;
        object ctxObj = RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.setup == null || p.character == null)
            {
                return null;
            }
            int nm = Nova.v != null && Nova.v.vehiclesModelName != null ? Nova.v.vehiclesModelName.Length : 0;
            return (object)new object[] { p.setup.transform.position, p.character.Id, nm };
        });
        if (ctxObj == null)
        {
            return "{\"error\":\"joueur introuvable ou pas en jeu\"}";
        }
        object[] ctx2 = (object[])ctxObj;
        Vector3 basePos = (Vector3)ctx2[0];
        int charId = (int)ctx2[1];
        int nModels = (int)ctx2[2];
        if (nModels <= 0)
        {
            return "{\"error\":\"liste des modèles indisponible\"}";
        }
        string permJson = "{\"owner\":{\"characterId\":" + charId + ",\"groupId\":0},\"coOwners\":[]}";
        List<Vehicles> rows = new List<Vehicles>();
        for (int i = 0; i < count; i++)
        {
            int modelId = i % Math.Min(nModels, 12); // varie les 12 premiers modèles
            try
            {
                Vehicles row = LifeDB.CreateVehicle(modelId, permJson).Result;
                if (row != null)
                {
                    rows.Add(row);
                }
            }
            catch
            {
            }
        }
        if (rows.Count == 0)
        {
            return "{\"error\":\"échec création en base\"}";
        }
        string result = (string)RunOnMain(delegate
        {
            List<int> ids = LoadBenchIds();
            int spawned = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                Vehicles row = rows[i];
                if (Nova.v.GetVehicle(row.Id) == null)
                {
                    Nova.v.vehicles.Add(new LifeVehicle
                    {
                        modelId = row.ModelId,
                        vehicleId = row.Id,
                        permissions = JsonUtility.FromJson<Permissions>(row.Permissions),
                        plate = row.Plate,
                        isStowed = true,
                        inventory = row.Inventory,
                        engineInventory = row.EngineInventory,
                        color = row.Color,
                        smoothness = row.Smoothness,
                        x = row.X,
                        y = row.Y,
                        z = row.Z,
                        rotX = row.RotX,
                        rotY = row.RotY,
                        rotZ = row.RotZ,
                        bizId = row.BizId,
                        damages = row.Damages,
                        fuel = row.Fuel,
                        serigraphie = row.Serigraphie,
                        eurosoftData = row.EurosoftData,
                        accessoriesData = row.AccessoriesData
                    });
                }
                // grille 6 par rangée, 6 m d'espacement, à ~10 m devant le joueur
                Vector3 pos = basePos + new Vector3((i % 6) * 6f - 15f, 0.5f, (i / 6) * 6f + 10f);
                bool ok = false;
                try { ok = Nova.v.UnstowVehicle(row.Id, pos, Quaternion.identity); } catch { }
                ids.Add(row.Id);
                if (ok)
                {
                    spawned++;
                }
            }
            SaveBenchIds(ids);
            StaffLog("test de charge : spawn de " + spawned + " véhicules près de " + steamId);
            Debug.Log("[TKWEB] BENCH spawn " + spawned + "/" + rows.Count + " véhicules (total test " + ids.Count + ")");
            return "{\"ok\":true,\"spawned\":" + spawned + ",\"total\":" + ids.Count + "}";
        });
        return result;
    }

    private string ApiBenchGhost()
    {
        return (string)RunOnMain(delegate
        {
            List<int> ids = LoadBenchIds();
            int done = 0, skipped = 0;
            foreach (int id in ids)
            {
                LifeVehicle lv = Nova.v.GetVehicle(id);
                if (lv != null && lv.instance != null && lv.fake == null && !lv.isStowed)
                {
                    bool ok = false;
                    try { ok = Nova.v.TryReplaceCarWithFake(lv.instance, false, false); } catch { }
                    if (ok) done++; else skipped++;
                }
            }
            StaffLog("test de charge : " + done + " véhicules passés en fantôme");
            return "{\"ok\":true,\"ghosted\":" + done + ",\"skipped\":" + skipped + "}";
        });
    }

    private string ApiBenchReal()
    {
        return (string)RunOnMain(delegate
        {
            List<int> ids = LoadBenchIds();
            int done = 0;
            foreach (int id in ids)
            {
                LifeVehicle lv = Nova.v.GetVehicle(id);
                if (lv != null && lv.fake != null)
                {
                    try { Nova.v.TryReplaceFakeWithCar(id); done++; } catch { }
                }
            }
            StaffLog("test de charge : " + done + " fantômes redevenus réels");
            return "{\"ok\":true,\"restored\":" + done + "}";
        });
    }

    private string ApiBenchClear()
    {
        List<int> ids = LoadBenchIds();
        if (ids.Count == 0)
        {
            return "{\"ok\":true,\"removed\":0,\"remaining\":0}";
        }
        // 1) repasser en réel + ranger (main thread) — best-effort, sans bloquer
        RunOnMain(delegate
        {
            foreach (int id in ids)
            {
                LifeVehicle lv = Nova.v.GetVehicle(id);
                if (lv == null)
                {
                    continue;
                }
                try
                {
                    if (lv.fake != null)
                    {
                        Nova.v.TryReplaceFakeWithCar(id);
                    }
                }
                catch
                {
                }
                try
                {
                    if (!lv.isStowed)
                    {
                        Nova.v.StowVehicle(id);
                    }
                }
                catch
                {
                }
            }
            return (object)null;
        });
        // 2) suppression en base (hors main thread)
        foreach (int id in ids)
        {
            try
            {
                LifeDB.RemoveVehicle(id).Wait();
            }
            catch
            {
            }
        }
        // 3) retrait du monde + recalcul de ce qui reste réellement (main thread).
        //    On ne garde dans bench.json QUE les véhicules encore présents
        //    (ex. occupés par un joueur) : jamais d'orphelins, et un second clic
        //    « Supprimer » réessaie proprement.
        return (string)RunOnMain(delegate
        {
            int removed = 0;
            List<int> remaining = new List<int>();
            foreach (int id in ids)
            {
                LifeVehicle lv = Nova.v.GetVehicle(id);
                if (lv != null)
                {
                    try
                    {
                        Nova.v.vehicles.Remove(lv);
                    }
                    catch
                    {
                    }
                    lv = Nova.v.GetVehicle(id);
                }
                if (lv == null)
                {
                    removed++;
                }
                else
                {
                    remaining.Add(id);
                }
            }
            SaveBenchIds(remaining);
            StaffLog("test de charge : " + removed + " véhicules supprimés" + (remaining.Count > 0 ? " (" + remaining.Count + " restants, occupés ?)" : ""));
            Debug.Log("[TKWEB] BENCH clear removed=" + removed + " remaining=" + remaining.Count);
            return "{\"ok\":true,\"removed\":" + removed + ",\"remaining\":" + remaining.Count + "}";
        });
    }

    private string ApiBenchStatus()
    {
        return (string)RunOnMain(delegate
        {
            List<int> ids = LoadBenchIds();
            int real = 0, ghosts = 0, missing = 0;
            foreach (int id in ids)
            {
                LifeVehicle lv = Nova.v.GetVehicle(id);
                if (lv == null)
                {
                    missing++;
                }
                else if (lv.fake != null)
                {
                    ghosts++;
                }
                else if (lv.instance != null)
                {
                    real++;
                }
            }
            return "{\"total\":" + ids.Count + ",\"real\":" + real + ",\"ghosts\":" + ghosts + ",\"missing\":" + missing + "}";
        });
    }

    // ------------------------------------------------------------------
    // FastDL images (v2.12) : cache des sérigraphies. Le serveur télécharge
    // chaque image une seule fois, la stocke dans Plugins/TKWebPanel/sericache/
    // et réécrit l'URL du véhicule vers publicUrl/img/<sha1>.<ext> — les
    // clients téléchargent alors depuis notre HTTPS rapide, et l'image
    // survit à l'expiration du lien d'origine (liens Discord notamment).
    // ------------------------------------------------------------------
    private readonly HashSet<string> seriFailed = new HashSet<string>();

    private string SeriBase()
    {
        string b = config.publicUrl;
        if (!b.EndsWith("/"))
        {
            b += "/";
        }
        return b + "img/";
    }

    private void StartSericache()
    {
        if (string.IsNullOrEmpty(config.publicUrl))
        {
            return;
        }
        try { Directory.CreateDirectory(Path.Combine(pluginDir, "sericache")); } catch { }
        Thread t = new Thread(delegate ()
        {
            Thread.Sleep(60 * 1000); // laisse le serveur finir de charger
            while (true)
            {
                try
                {
                    SericachePass();
                }
                catch (Exception ex)
                {
                    Debug.LogError("[TKWEB] Erreur cache sérigraphies : " + ex.Message);
                }
                Thread.Sleep(5 * 60 * 1000);
            }
        });
        t.IsBackground = true;
        t.Start();
        Debug.Log("[TKWEB] FastDL images actif : sérigraphies mises en cache vers " + SeriBase());
    }

    private void SericachePass()
    {
        string baseUrl = SeriBase();
        object listObj = RunOnMain(delegate
        {
            List<object[]> found = new List<object[]>();
            if (Nova.v == null || Nova.v.vehicles == null)
            {
                return (object)found;
            }
            foreach (LifeVehicle lv in Nova.v.vehicles)
            {
                if (lv == null)
                {
                    continue;
                }
                string u = lv.serigraphie;
                if (string.IsNullOrEmpty(u))
                {
                    continue;
                }
                if (!u.StartsWith("http://") && !u.StartsWith("https://"))
                {
                    continue;
                }
                if (u.StartsWith(baseUrl))
                {
                    continue; // déjà en cache
                }
                if (found.Count >= 10)
                {
                    break; // passe douce, la suite au prochain cycle
                }
                found.Add(new object[] { lv.vehicleId, u });
            }
            return (object)found;
        });
        List<object[]> jobs = (List<object[]>)listObj;
        if (jobs == null || jobs.Count == 0)
        {
            return;
        }
        int done = 0;
        foreach (object[] job in jobs)
        {
            int vid = (int)job[0];
            string url = (string)job[1];
            if (seriFailed.Contains(url))
            {
                continue;
            }
            string newUrl = SericacheFetch(url);
            if (newUrl == null)
            {
                seriFailed.Add(url); // on ne réessaie pas avant le prochain boot
                continue;
            }
            RunOnMain(delegate
            {
                LifeVehicle lv = Nova.v.GetVehicle(vid);
                if (lv != null && lv.serigraphie == url)
                {
                    lv.serigraphie = newUrl;
                    try
                    {
                        if (lv.instance != null)
                        {
                            lv.instance.Networkserigraphie = newUrl;
                        }
                    }
                    catch
                    {
                    }
                }
                return (object)null;
            });
            done++;
        }
        if (done > 0)
        {
            Debug.Log("[TKWEB] SERICACHE " + done + " sérigraphie(s) mises en cache et réécrites");
        }
    }

    // Télécharge l'image (5 Mo max, png/jpg seulement) et renvoie l'URL cache, sinon null.
    private string SericacheFetch(string url)
    {
        try
        {
            string hash;
            using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
            }
            string dir = Path.Combine(pluginDir, "sericache");
            string pngFile = Path.Combine(dir, hash + ".png");
            string jpgFile = Path.Combine(dir, hash + ".jpg");
            if (File.Exists(pngFile))
            {
                return SeriBase() + hash + ".png";
            }
            if (File.Exists(jpgFile))
            {
                return SeriBase() + hash + ".jpg";
            }
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "Mozilla/5.0 (Nova-Life server image cache)";
            req.AllowAutoRedirect = true;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode != HttpStatusCode.OK || resp.ContentLength > 5 * 1024 * 1024)
                {
                    return null;
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    Stream st = resp.GetResponseStream();
                    byte[] buf = new byte[16384];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > 5 * 1024 * 1024)
                        {
                            return null;
                        }
                    }
                    byte[] data = ms.ToArray();
                    if (data.Length < 100)
                    {
                        return null;
                    }
                    bool isPng = data[0] == 0x89 && data[1] == 0x50;
                    bool isJpg = data[0] == 0xFF && data[1] == 0xD8;
                    if (!isPng && !isJpg)
                    {
                        return null; // le client ne charge que png/jpg
                    }
                    File.WriteAllBytes(isPng ? pngFile : jpgFile, data);
                    return SeriBase() + hash + (isPng ? ".png" : ".jpg");
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private void ServeSerigraphie(HttpListenerContext ctx, string path)
    {
        try
        {
            string name = Path.GetFileName(path);
            if (!Regex.IsMatch(name, "^[0-9a-f]{40}\\.(png|jpg)$"))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.OutputStream.Close();
                return;
            }
            string file = Path.Combine(Path.Combine(pluginDir, "sericache"), name);
            if (!File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.OutputStream.Close();
                return;
            }
            byte[] data = File.ReadAllBytes(file);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = name.EndsWith(".png") ? "image/png" : "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=2592000";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    // Ban IP d'un joueur en ligne : écrit dans TKAntiFlood/banned.txt (permanent)
    // et déconnecte le joueur. TKAntiFlood v1.2 recharge le fichier à chaud.
    private string ApiBanIp(string body)
    {
        string steamId = Json.GetString(body, "steamId", "");
        object ipObj = RunOnMain(delegate
        {
            Player p = FindPlayer(steamId);
            if (p == null || p.conn == null)
            {
                return null;
            }
            string a = null;
            try
            {
                NetworkConnectionToClient toClient = p.conn as NetworkConnectionToClient;
                a = toClient != null ? toClient.address : null;
            }
            catch { }
            try { p.Disconnect("Bannissement IP"); } catch { }
            return (object)a;
        });
        if (ipObj == null)
        {
            return "{\"error\":\"joueur introuvable (il doit être en ligne pour récupérer son IP)\"}";
        }
        string ip = (string)ipObj;
        if (ip.StartsWith("::ffff:"))
        {
            ip = ip.Substring(7);
        }
        int colon = ip.LastIndexOf(':');
        if (colon > 0 && ip.IndexOf('.') > 0 && colon > ip.IndexOf('.'))
        {
            ip = ip.Substring(0, colon);
        }
        if (!Regex.IsMatch(ip, "^[0-9a-fA-F.:]+$") || ip == "127.0.0.1" || ip == "::1")
        {
            return "{\"error\":\"IP invalide\"}";
        }
        try
        {
            string f = Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), "TKAntiFlood"), "banned.txt");
            string existing = File.Exists(f) ? File.ReadAllText(f) : "";
            if (!existing.Contains(ip + ";"))
            {
                File.AppendAllText(f, (existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "") + ip + ";0\n");
            }
            StaffLog("bannit l'IP " + ip + " (" + steamId + ")");
            Debug.Log("[TKWEB] BANIP " + ip + " (" + steamId + ")");
            return "{\"ok\":true,\"ip\":" + Json.Str(ip) + "}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    // Bannit une IP saisie directement (depuis le journal d'identités ou
    // le champ manuel de la section Sécurité) + coupe tout joueur en ligne
    // depuis cette IP. Réutilise banned.txt de TKAntiFlood (rechargé à chaud).
    private string ApiBanIpRaw(string body)
    {
        string ip = Json.GetString(body, "ip", "").Trim();
        if (ip.StartsWith("::ffff:"))
        {
            ip = ip.Substring(7);
        }
        if (!Regex.IsMatch(ip, @"^[0-9]{1,3}(\.[0-9]{1,3}){3}$") && !Regex.IsMatch(ip, @"^[0-9a-fA-F:]+$"))
        {
            return "{\"error\":\"IP invalide (ex : 45.134.79.117)\"}";
        }
        if (ip == "127.0.0.1" || ip == "::1")
        {
            return "{\"error\":\"IP locale refusée\"}";
        }
        try
        {
            string f = Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), "TKAntiFlood"), "banned.txt");
            string existing = File.Exists(f) ? File.ReadAllText(f) : "";
            if (!existing.Contains(ip + ";"))
            {
                File.AppendAllText(f, (existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "") + ip + ";0\n");
            }
            // couper les joueurs actuellement connectés depuis cette IP
            string wantIp = ip;
            int kicked = (int)RunOnMain(delegate
            {
                int n = 0;
                try
                {
                    foreach (Player p in Nova.server.GetAllPlayers())
                    {
                        if (p == null || p.conn == null) continue;
                        string a = null;
                        try { NetworkConnectionToClient c = p.conn as NetworkConnectionToClient; a = c != null ? c.address : null; } catch { }
                        if (a == null) continue;
                        if (a.StartsWith("::ffff:")) a = a.Substring(7);
                        int col = a.LastIndexOf(':');
                        if (col > 0 && a.IndexOf('.') > 0 && col > a.IndexOf('.')) a = a.Substring(0, col);
                        if (a == wantIp)
                        {
                            try { p.Disconnect("Bannissement IP"); n++; } catch { }
                        }
                    }
                }
                catch { }
                return (object)n;
            });
            StaffLog("bannit l'IP " + ip + " (manuel)" + (kicked > 0 ? " — " + kicked + " joueur(s) déconnecté(s)" : ""));
            Debug.Log("[TKWEB] BANIPRAW " + ip + " kicked=" + kicked);
            return "{\"ok\":true,\"ip\":" + Json.Str(ip) + ",\"kicked\":" + kicked + "}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    // ------------------------------------------------------------------
    // Journal d'identité IP <-> SteamID (v2.15). Un thread de fond note
    // toutes les 20 s le (steamId, pseudo, IP) de chaque joueur en ligne.
    // Permet de croiser : les IP d'un compte, et les comptes vus sur une IP
    // (détection d'alts et de contournements de ban). identities.tsv :
    // steamId \t pseudo \t ip \t premierVu(unix) \t dernierVu(unix)
    // ------------------------------------------------------------------
    private class IdentIp { public long first; public long last; }
    private class Ident { public string name; public Dictionary<string, IdentIp> ips = new Dictionary<string, IdentIp>(); }
    private readonly Dictionary<string, Ident> identities = new Dictionary<string, Ident>();
    private readonly object identLock = new object();
    private bool identDirty;

    private string IdentPath() { return Path.Combine(pluginDir, "identities.tsv"); }

    private static long NowUnix()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    private static string NormIp(string a)
    {
        if (string.IsNullOrEmpty(a)) return null;
        if (a.StartsWith("::ffff:")) a = a.Substring(7);
        int c = a.LastIndexOf(':');
        if (c > 0 && a.IndexOf('.') > 0 && c > a.IndexOf('.')) a = a.Substring(0, c);
        return a;
    }

    private void LoadIdentities()
    {
        try
        {
            if (!File.Exists(IdentPath())) return;
            foreach (string line in File.ReadAllLines(IdentPath()))
            {
                string[] p = line.Split('\t');
                if (p.Length < 5) continue;
                string sid = p[0].Trim();
                if (sid.Length == 0) continue;
                Ident id;
                if (!identities.TryGetValue(sid, out id)) { id = new Ident(); identities[sid] = id; }
                id.name = p[1];
                long f, l;
                long.TryParse(p[3], out f); long.TryParse(p[4], out l);
                id.ips[p[2]] = new IdentIp { first = f, last = l };
            }
        }
        catch (Exception ex) { Debug.LogError("[TKWEB] Lecture identities.tsv : " + ex.Message); }
    }

    private void SaveIdentities()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            lock (identLock)
            {
                foreach (KeyValuePair<string, Ident> kv in identities)
                    foreach (KeyValuePair<string, IdentIp> ip in kv.Value.ips)
                        sb.Append(kv.Key).Append('\t').Append((kv.Value.name ?? "").Replace('\t', ' ').Replace('\n', ' '))
                          .Append('\t').Append(ip.Key).Append('\t').Append(ip.Value.first).Append('\t').Append(ip.Value.last).Append('\n');
            }
            File.WriteAllText(IdentPath(), sb.ToString());
        }
        catch (Exception ex) { Debug.LogError("[TKWEB] Ecriture identities.tsv : " + ex.Message); }
    }

    // ------------------------------------------------------------------
    // Journal du serveur (v3.7). Deux niveaux :
    //  - un tampon mémoire de TOUTES les lignes de la console (4000 max,
    //    perdu au restart) pour la vue « En direct » ;
    //  - une archive sur disque des lignes IMPORTANTES (alertes TKFLOOD/
    //    TKCHEAT, actions TKWEB, erreurs) dans serverlog/AAAA-MM-JJ.log,
    //    conservée 30 jours, pour l'historique.
    private static readonly object logBufLock = new object();
    private static readonly List<string[]> logBuf = new List<string[]>();
    private static bool logBufStarted;
    private static string logLastFileMsg = "";
    private static long logFileBytesToday;
    private static string logFileDay = "";

    private static bool LogIsImportant(string msg, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception)
        {
            return true;
        }
        return msg.IndexOf("[TKFLOOD]", StringComparison.Ordinal) >= 0
            || msg.IndexOf("[TKCHEAT]", StringComparison.Ordinal) >= 0
            || msg.IndexOf("[TKWEB]", StringComparison.Ordinal) >= 0
            || msg.IndexOf("[TKGHOST]", StringComparison.Ordinal) >= 0
            || msg.IndexOf("ALERTE", StringComparison.Ordinal) >= 0;
    }

    private void StartLogBuffer()
    {
        lock (logBufLock)
        {
            if (logBufStarted)
            {
                return;
            }
            logBufStarted = true;
        }
        string dir = Path.Combine(pluginDir, "serverlog");
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // rétention : purge des fichiers de plus de 30 jours
            foreach (string f in Directory.GetFiles(dir, "*.log"))
            {
                try
                {
                    if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 30)
                    {
                        File.Delete(f);
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
        Application.logMessageReceivedThreaded += delegate (string condition, string stackTrace, LogType type)
        {
            try
            {
                if (string.IsNullOrEmpty(condition))
                {
                    return;
                }
                string msg = condition.Replace("\r", " ").Replace("\n", " ");
                if (msg.Length > 500)
                {
                    msg = msg.Substring(0, 500);
                }
                string hhmm = DateTime.Now.ToString("HH:mm:ss");
                lock (logBufLock)
                {
                    logBuf.Add(new string[] { hhmm, type.ToString(), msg });
                    if (logBuf.Count > 4000)
                    {
                        logBuf.RemoveRange(0, 1000);
                    }
                    if (LogIsImportant(msg, type) && msg != logLastFileMsg)
                    {
                        logLastFileMsg = msg;
                        string day = DateTime.Now.ToString("yyyy-MM-dd");
                        if (day != logFileDay)
                        {
                            logFileDay = day;
                            logFileBytesToday = 0;
                        }
                        // garde-fou : 5 Mo/jour max (un spam d'erreurs ne remplit pas le disque)
                        if (logFileBytesToday < 5 * 1024 * 1024)
                        {
                            string line = hhmm + "|" + type + "|" + msg + "\n";
                            File.AppendAllText(Path.Combine(dir, day + ".log"), line);
                            logFileBytesToday += line.Length;
                        }
                    }
                }
            }
            catch
            {
            }
        };
    }

    private string ApiServerLog(string body)
    {
        string q = Json.GetString(body, "q", "").Trim().ToLowerInvariant();
        string mode = Json.GetString(body, "mode", "all");
        string day = Json.GetString(body, "day", "").Trim();
        string dir = Path.Combine(pluginDir, "serverlog");
        // liste des jours archivés disponibles
        List<string> days = new List<string>();
        try
        {
            foreach (string f in Directory.GetFiles(dir, "*.log"))
            {
                days.Add(Path.GetFileNameWithoutExtension(f));
            }
            days.Sort();
            days.Reverse();
        }
        catch
        {
        }
        List<string[]> snap = new List<string[]>();
        if (day.Length > 0)
        {
            // historique : lecture du fichier du jour demandé
            if (!Regex.IsMatch(day, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$"))
            {
                return "{\"error\":\"jour invalide\"}";
            }
            string path = Path.Combine(dir, day + ".log");
            if (File.Exists(path))
            {
                string[] all = File.ReadAllLines(path);
                int start = all.Length > 3000 ? all.Length - 3000 : 0;
                for (int i = start; i < all.Length; i++)
                {
                    string[] parts = all[i].Split(new char[] { '|' }, 3);
                    if (parts.Length == 3)
                    {
                        snap.Add(parts);
                    }
                }
            }
        }
        else
        {
            lock (logBufLock)
            {
                snap.AddRange(logBuf);
            }
        }
        List<string> outp = new List<string>();
        for (int i = snap.Count - 1; i >= 0 && outp.Count < 400; i--)
        {
            string[] e = snap[i];
            LogType approx = (e[1] == "Error" || e[1] == "Exception") ? LogType.Error : LogType.Log;
            if (mode == "alerts" && !LogIsImportant(e[2], approx))
            {
                continue;
            }
            if (q.Length > 0 && e[2].ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0)
            {
                continue;
            }
            outp.Add("{\"t\":" + Json.Str(e[0]) + ",\"ty\":" + Json.Str(e[1]) + ",\"m\":" + Json.Str(e[2]) + "}");
        }
        outp.Reverse();
        List<string> dj = new List<string>();
        foreach (string d in days)
        {
            dj.Add(Json.Str(d));
        }
        return "{\"lines\":[" + string.Join(",", outp.ToArray()) + "],\"total\":" + snap.Count
            + ",\"days\":[" + string.Join(",", dj.ToArray()) + "]}";
    }

    private void StartIdentityLogger()
    {
        LoadIdentities();
        Thread t = new Thread(delegate ()
        {
            Thread.Sleep(30 * 1000);
            while (true)
            {
                try
                {
                    object snap = RunOnMain(delegate
                    {
                        List<string> rows = new List<string>();
                        if (Nova.server != null)
                        {
                            foreach (Player p in Nova.server.GetAllPlayers())
                            {
                                if (p == null || p.steamId == 0) continue;
                                string ip = null;
                                try { NetworkConnectionToClient c = p.conn as NetworkConnectionToClient; ip = c != null ? c.address : null; } catch { }
                                ip = NormIp(ip);
                                if (string.IsNullOrEmpty(ip)) continue;
                                rows.Add(p.steamId + "\t" + (p.steamUsername ?? "") + "\t" + ip);
                            }
                        }
                        return (object)rows;
                    });
                    List<string> list = (List<string>)snap;
                    if (list != null && list.Count > 0)
                    {
                        long now = NowUnix();
                        lock (identLock)
                        {
                            foreach (string r in list)
                            {
                                string[] pr = r.Split('\t');
                                if (pr.Length < 3) continue;
                                Ident id;
                                if (!identities.TryGetValue(pr[0], out id)) { id = new Ident(); identities[pr[0]] = id; identDirty = true; }
                                if (pr[1].Length > 0 && id.name != pr[1]) { id.name = pr[1]; identDirty = true; }
                                IdentIp e;
                                if (!id.ips.TryGetValue(pr[2], out e)) { e = new IdentIp { first = now, last = now }; id.ips[pr[2]] = e; identDirty = true; }
                                else { e.last = now; }
                            }
                        }
                        if (identDirty) { identDirty = false; SaveIdentities(); }
                    }
                }
                catch (Exception ex) { Debug.LogError("[TKWEB] Journal identité : " + ex.Message); }
                Thread.Sleep(20 * 1000);
            }
        });
        t.IsBackground = true;
        t.Start();
        Debug.Log("[TKWEB] Journal identité IP<->SteamID actif");
    }

    private string ApiIdentity(string steamId, string ip)
    {
        lock (identLock)
        {
            if (!string.IsNullOrEmpty(ip))
            {
                string want = NormIp(ip.Trim());
                StringBuilder sb = new StringBuilder("{\"ip\":" + Json.Str(want) + ",\"accounts\":[");
                bool first = true;
                foreach (KeyValuePair<string, Ident> kv in identities)
                {
                    IdentIp e;
                    if (!kv.Value.ips.TryGetValue(want, out e)) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"steamId\":").Append(Json.Str(kv.Key));
                    sb.Append(",\"username\":").Append(Json.Str(kv.Value.name ?? ""));
                    sb.Append(",\"first\":").Append(e.first).Append(",\"last\":").Append(e.last).Append("}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
            string sid = (steamId ?? "").Trim();
            Ident id;
            if (!identities.TryGetValue(sid, out id))
            {
                return "{\"steamId\":" + Json.Str(sid) + ",\"username\":\"\",\"ips\":[]}";
            }
            StringBuilder sb2 = new StringBuilder("{\"steamId\":" + Json.Str(sid) + ",\"username\":" + Json.Str(id.name ?? "") + ",\"ips\":[");
            bool f2 = true;
            foreach (KeyValuePair<string, IdentIp> ipkv in id.ips)
            {
                int shared = 0;
                foreach (KeyValuePair<string, Ident> other in identities)
                    if (other.Key != sid && other.Value.ips.ContainsKey(ipkv.Key)) shared++;
                if (!f2) sb2.Append(",");
                f2 = false;
                sb2.Append("{\"ip\":").Append(Json.Str(ipkv.Key));
                sb2.Append(",\"first\":").Append(ipkv.Value.first).Append(",\"last\":").Append(ipkv.Value.last);
                sb2.Append(",\"shared\":").Append(shared).Append("}");
            }
            sb2.Append("]}");
            return sb2.ToString();
        }
    }

    // ------------------------------------------------------------------
    // Live map (v3.1) : image de la carte du jeu (map.jpg, extraite des
    // assets, 3500x3500) servie par /mapimg ; calibrage monde->pixels
    // persisté PAR INSTANCE dans Plugins/TKWebPanel/mapcalib.json
    // (chaque serveur peut avoir sa propre carte). Le POST est réservé
    // au propriétaire (calibrage en 2 points depuis le panel).
    // ------------------------------------------------------------------
    private void ServeMapImage(HttpListenerContext ctx)
    {
        try
        {
            string file = Path.Combine(pluginDir, "map.jpg");
            if (!File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.OutputStream.Close();
                return;
            }
            byte[] data = File.ReadAllBytes(file);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = "public, max-age=604800";
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.OutputStream.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.OutputStream.Close(); } catch { }
        }
    }

    private string MapCalibPath() { return Path.Combine(pluginDir, "mapcalib.json"); }

    private string ApiMapCalibGet()
    {
        try
        {
            if (File.Exists(MapCalibPath()))
            {
                string t = File.ReadAllText(MapCalibPath()).Trim();
                if (t.StartsWith("{"))
                {
                    return t;
                }
            }
        }
        catch
        {
        }
        // estimation par défaut (carte Amboise, calage automatique par corrélation)
        return "{\"sx\":0.45,\"sy\":-0.45,\"ox\":1626,\"oy\":1357,\"defaut\":true}";
    }

    private string ApiMapCalibSet(string body)
    {
        double sx = Json.GetDouble(body, "sx", 0);
        double sy = Json.GetDouble(body, "sy", 0);
        double ox2 = Json.GetDouble(body, "ox", 0);
        double oy2 = Json.GetDouble(body, "oy", 0);
        if (sx == 0 || sy == 0 || Math.Abs(sx) > 50 || Math.Abs(sy) > 50)
        {
            return "{\"error\":\"calibrage invalide\"}";
        }
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllText(MapCalibPath(),
                "{\"sx\":" + sx.ToString("0.######", ci) + ",\"sy\":" + sy.ToString("0.######", ci)
                + ",\"ox\":" + ox2.ToString("0.##", ci) + ",\"oy\":" + oy2.ToString("0.##", ci) + "}");
            StaffLog("calibre la carte (sx=" + sx.ToString("0.####", ci) + ")");
            return "{\"ok\":true}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    private string ApiMapVehicles()
    {
        return (string)RunOnMain(delegate
        {
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            HashSet<int> seen = new HashSet<int>();
            if (Nova.v != null && Nova.v.vehicles != null)
            {
                foreach (LifeVehicle lv in Nova.v.vehicles)
                {
                    if (lv == null || lv.isStowed || !seen.Add(lv.vehicleId))
                    {
                        continue;
                    }
                    float x = 0, z = 0;
                    bool ghost = false;
                    try
                    {
                        if (lv.instance != null)
                        {
                            UnityEngine.Vector3 pos = lv.instance.transform.position;
                            x = pos.x; z = pos.z;
                        }
                        else if (lv.fake != null)
                        {
                            UnityEngine.Vector3 pos = lv.fake.transform.position;
                            x = pos.x; z = pos.z;
                            ghost = true;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                    if (float.IsNaN(x) || float.IsNaN(z))
                    {
                        continue;
                    }
                    int ownerId = 0;
                    try { ownerId = lv.permissions != null && lv.permissions.owner != null ? lv.permissions.owner.characterId : 0; } catch { }
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"id\":").Append(lv.vehicleId);
                    sb.Append(",\"name\":").Append(Json.Str(VehicleModelName(lv.modelId)));
                    sb.Append(",\"plate\":").Append(Json.Str(lv.plate ?? ""));
                    sb.Append(",\"x\":").Append(x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"z\":").Append(z.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append(",\"ghost\":").Append(ghost ? "true" : "false");
                    sb.Append(",\"ownerId\":").Append(ownerId);
                    sb.Append("}");
                }
            }
            sb.Append("]");
            return sb.ToString();
        });
    }

    // Alertes anti-cheat marquées « faux positif » depuis le panel : on ne
    // touche pas au fichier de TKAntiCheat (il le réécrit), on garde une
    // liste de clés "time|steamId" côté panel et le client filtre l'affichage.
    private string AcDismissedPath() { return Path.Combine(pluginDir, "acdismissed.txt"); }

    private string ApiAcDismissed()
    {
        try
        {
            if (!File.Exists(AcDismissedPath()))
            {
                return "[]";
            }
            StringBuilder sb = new StringBuilder("[");
            bool first = true;
            foreach (string line in File.ReadAllLines(AcDismissedPath()))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append(Json.Str(t));
            }
            sb.Append("]");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    private string ApiAcDismiss(string body)
    {
        string time = Json.GetString(body, "time", "").Trim();
        string steamId = Json.GetString(body, "steamId", "").Trim();
        if (time.Length == 0 || !Regex.IsMatch(steamId, "^[0-9]{5,20}$"))
        {
            return "{\"error\":\"alerte invalide\"}";
        }
        try
        {
            List<string> lines = new List<string>();
            if (File.Exists(AcDismissedPath()))
            {
                lines.AddRange(File.ReadAllLines(AcDismissedPath()));
            }
            string key = time + "|" + steamId;
            if (!lines.Contains(key))
            {
                lines.Add(key);
            }
            while (lines.Count > 500)
            {
                lines.RemoveAt(0);
            }
            File.WriteAllLines(AcDismissedPath(), lines.ToArray());
            StaffLog("marque l'alerte anti-cheat " + key + " comme faux positif");
            return "{\"ok\":true}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    // ------------------------------------------------------------------
    // Classements RP (v3.3) : agrège fortune, entreprises, immobilier,
    // véhicules, niveau, récolte, kills/morts (journal 92 j) par personnage.
    // Calcul coûteux -> cache 5 minutes.
    // ------------------------------------------------------------------
    private static string rankingsCache;
    private static DateTime rankingsCacheTime = DateTime.MinValue;
    private static readonly object rankingsLock = new object();

    private class RankChar
    {
        public int Id { get; set; }
        public int AccountId { get; set; }
        public string Firstname { get; set; }
        public string Lastname { get; set; }
        public string Inventory { get; set; }
        public double Bank { get; set; }
        public int Level { get; set; }
        public int XP { get; set; }
        public int StatRock { get; set; }
        public int StatTree { get; set; }
        public int StatCopper { get; set; }
        public int StatDiamond { get; set; }
        public double WorkTime { get; set; }
    }
    private class RankAcct
    {
        public int Id { get; set; }
        public string SteamId { get; set; }
        public string Username { get; set; }
    }
    private class RankBiz
    {
        public int OwnerId { get; set; }
        public int Nb { get; set; }
        public double Total { get; set; }
    }
    private class RankPerm
    {
        public string Permissions { get; set; }
        public double Price { get; set; }
    }

    private string ApiRankings()
    {
        lock (rankingsLock)
        {
            if (rankingsCache != null && (DateTime.Now - rankingsCacheTime).TotalSeconds < 300)
            {
                return rankingsCache;
            }
        }
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var kills = new Dictionary<string, int>();
            var deaths = new Dictionary<string, int>();
            try
            {
                if (actDir != null && Directory.Exists(actDir))
                {
                    Regex kr = new Regex(@"\] KILL — .* \((\d{15,20})\) : .* \((\d{15,20})\)");
                    foreach (string f in Directory.GetFiles(actDir, "activity-*.log"))
                    {
                        foreach (string line in File.ReadAllLines(f))
                        {
                            Match m = kr.Match(line);
                            if (!m.Success) continue;
                            string k = m.Groups[1].Value, v = m.Groups[2].Value;
                            int n;
                            kills.TryGetValue(k, out n); kills[k] = n + 1;
                            deaths.TryGetValue(v, out n); deaths[v] = n + 1;
                        }
                    }
                }
            }
            catch
            {
            }

            SQLite.SQLiteConnection conn = new SQLite.SQLiteConnection(DbPath(), SQLite.SQLiteOpenFlags.ReadOnly, false);
            try
            {
                var accts = new Dictionary<int, RankAcct>();
                foreach (RankAcct a in conn.Query<RankAcct>("SELECT Id, SteamId, Username FROM Accounts"))
                {
                    accts[a.Id] = a;
                }
                var bizs = new Dictionary<int, RankBiz>();
                foreach (RankBiz b in conn.Query<RankBiz>("SELECT OwnerId, COUNT(*) as Nb, SUM(Bank) as Total FROM Bizs WHERE OwnerId > 0 GROUP BY OwnerId"))
                {
                    bizs[b.OwnerId] = b;
                }
                Regex ownRe = new Regex("\"characterId\"\\s*:\\s*(\\d+)");
                var areaCount = new Dictionary<int, int>();
                var areaValue = new Dictionary<int, double>();
                foreach (RankPerm a in conn.Query<RankPerm>("SELECT Permissions, Price FROM Areas WHERE Permissions IS NOT NULL AND Permissions != ''"))
                {
                    Match m = ownRe.Match(a.Permissions ?? "");
                    if (!m.Success) continue;
                    int oid = int.Parse(m.Groups[1].Value);
                    if (oid <= 0) continue;
                    int c; areaCount.TryGetValue(oid, out c); areaCount[oid] = c + 1;
                    double v; areaValue.TryGetValue(oid, out v); areaValue[oid] = v + a.Price;
                }
                var vehCount = new Dictionary<int, int>();
                foreach (RankPerm vh in conn.Query<RankPerm>("SELECT Permissions, 0 as Price FROM Vehicles WHERE Permissions IS NOT NULL AND Permissions != ''"))
                {
                    Match m = ownRe.Match(vh.Permissions ?? "");
                    if (!m.Success) continue;
                    int oid = int.Parse(m.Groups[1].Value);
                    if (oid <= 0) continue;
                    int c; vehCount.TryGetValue(oid, out c); vehCount[oid] = c + 1;
                }

                StringBuilder sb = new StringBuilder("[");
                bool first = true;
                foreach (RankChar c in conn.Query<RankChar>(
                    "SELECT Id, AccountId, Firstname, Lastname, Inventory, Bank, Level, XP, StatRock, StatTree, StatCopper, StatDiamond, WorkTime FROM Characters"))
                {
                    if (string.IsNullOrEmpty(c.Firstname))
                    {
                        continue;
                    }
                    RankAcct a;
                    accts.TryGetValue(c.AccountId, out a);
                    double money = WalletMoney(c.Inventory);
                    RankBiz bz; bizs.TryGetValue(c.Id, out bz);
                    int ac; areaCount.TryGetValue(c.Id, out ac);
                    double av; areaValue.TryGetValue(c.Id, out av);
                    int vc; vehCount.TryGetValue(c.Id, out vc);
                    int kk = 0, dd = 0;
                    if (a != null && a.SteamId != null)
                    {
                        kills.TryGetValue(a.SteamId, out kk);
                        deaths.TryGetValue(a.SteamId, out dd);
                    }
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"id\":").Append(c.Id);
                    sb.Append(",\"name\":").Append(Json.Str((c.Firstname + " " + c.Lastname).Trim()));
                    sb.Append(",\"username\":").Append(Json.Str(a != null ? (a.Username ?? "") : ""));
                    sb.Append(",\"steamId\":").Append(Json.Str(a != null ? (a.SteamId ?? "") : ""));
                    sb.Append(",\"money\":").Append(money.ToString("0", ci));
                    sb.Append(",\"bank\":").Append(c.Bank.ToString("0", ci));
                    sb.Append(",\"level\":").Append(c.Level);
                    sb.Append(",\"xp\":").Append(c.XP);
                    sb.Append(",\"bizCount\":").Append(bz != null ? bz.Nb : 0);
                    sb.Append(",\"bizBank\":").Append((bz != null ? bz.Total : 0).ToString("0", ci));
                    sb.Append(",\"areaCount\":").Append(ac);
                    sb.Append(",\"areaValue\":").Append(av.ToString("0", ci));
                    sb.Append(",\"vehCount\":").Append(vc);
                    sb.Append(",\"harvest\":").Append(c.StatRock + c.StatTree + c.StatCopper + c.StatDiamond);
                    sb.Append(",\"workTime\":").Append(c.WorkTime.ToString("0", ci));
                    sb.Append(",\"kills\":").Append(kk);
                    sb.Append(",\"deaths\":").Append(dd);
                    sb.Append("}");
                }
                sb.Append("]");
                string result = sb.ToString();
                lock (rankingsLock)
                {
                    rankingsCache = result;
                    rankingsCacheTime = DateTime.Now;
                }
                return result;
            }
            finally
            {
                conn.Close();
            }
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }

    private string ApiAntiCheat()
    {
        try
        {
            string file = Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), "TKAntiCheat"), "alerts.json");
            if (File.Exists(file))
            {
                string content = File.ReadAllText(file).Trim();
                return string.IsNullOrEmpty(content) ? "[]" : content;
            }
        }
        catch
        {
        }
        return "[]";
    }

    private string FloodBansPath()
    {
        return Path.Combine(Path.Combine(Path.GetDirectoryName(pluginDir), "TKAntiFlood"), "banned.txt");
    }

    private string ApiFloodBans()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        try
        {
            string file = FloodBansPath();
            if (File.Exists(file))
            {
                bool first = true;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    string[] parts = line.Split(';');
                    if (!first)
                    {
                        sb.Append(",");
                    }
                    first = false;
                    sb.Append("{\"ip\":").Append(Json.Str(parts[0].Trim()));
                    sb.Append(",\"expiry\":").Append(parts.Length > 1 ? parts[1].Trim() : "0").Append("}");
                }
            }
        }
        catch
        {
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string ApiFloodUnban(string body)
    {
        string ip = Json.GetString(body, "ip", "");
        if (string.IsNullOrEmpty(ip))
        {
            return "{\"error\":\"ip manquante\"}";
        }
        try
        {
            string file = FloodBansPath();
            if (!File.Exists(file))
            {
                return "{\"error\":\"aucun fichier de bans\"}";
            }
            List<string> kept = new List<string>();
            foreach (string line in File.ReadAllLines(file))
            {
                if (!line.Trim().StartsWith(ip + ";"))
                {
                    kept.Add(line);
                }
            }
            File.WriteAllLines(file, kept.ToArray());
            Debug.Log("[TKWEB] FLOOD-UNBAN ip=" + ip + " (effectif au prochain redémarrage, ou immédiat si l'IP n'est pas en mémoire)");
            return "{\"ok\":true,\"note\":\"retiré du fichier ; le ban en mémoire reste actif jusqu'au redémarrage\"}";
        }
        catch (Exception ex)
        {
            return "{\"error\":" + Json.Str(ex.Message) + "}";
        }
    }
}

// Exécute les actions du panel sur le thread principal + mesure FPS/CPU
public class TKWebPanelDispatcher : MonoBehaviour
{
    public int allocatedCores = 3;

    private readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
    private float fpsEma = -1f;
    private Process process;
    private TimeSpan lastCpuTime;
    private float lastWallTime;
    private float cpuPercent = -1f;
    private float cpuAccum;

    public float ActualFps
    {
        get { return fpsEma > 0f ? fpsEma : 0f; }
    }

    public float CpuPercent
    {
        get { return cpuPercent; }
    }

    public void Enqueue(Action action)
    {
        queue.Enqueue(action);
    }

    private void Start()
    {
        try
        {
            process = Process.GetCurrentProcess();
            lastCpuTime = process.TotalProcessorTime;
            lastWallTime = Time.realtimeSinceStartup;
        }
        catch
        {
            process = null;
        }
    }

    private void Update()
    {
        Action action;
        int guard = 0;
        while (queue.TryDequeue(out action) && guard++ < 32)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogError("[TKWEB] Erreur action panel : " + ex.Message);
            }
        }

        float dt = Time.unscaledDeltaTime;
        if (dt > 0f)
        {
            float fps = 1f / dt;
            fpsEma = fpsEma < 0f ? fps : fpsEma * 0.95f + fps * 0.05f;
        }

        cpuAccum += dt;
        if (cpuAccum >= 5f && process != null)
        {
            cpuAccum = 0f;
            try
            {
                TimeSpan cpuNow = process.TotalProcessorTime;
                float wallNow = Time.realtimeSinceStartup;
                double wallDelta = wallNow - lastWallTime;
                if (wallDelta > 0.5)
                {
                    int cores = allocatedCores > 0 ? allocatedCores : 1;
                    cpuPercent = (float)(100.0 * (cpuNow - lastCpuTime).TotalSeconds / (wallDelta * cores));
                }
                lastCpuTime = cpuNow;
                lastWallTime = wallNow;
            }
            catch
            {
                process = null;
            }
        }
    }
}

// Helpers JSON minimalistes (écriture + extraction par regex)
public static class Json
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

    public static string GetString(string json, string key, string defaultValue)
    {
        if (string.IsNullOrEmpty(json))
        {
            return defaultValue;
        }
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
        if (!m.Success)
        {
            return defaultValue;
        }
        return m.Groups["v"].Value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    public static int GetInt(string json, string key, int defaultValue)
    {
        if (string.IsNullOrEmpty(json))
        {
            return defaultValue;
        }
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+)");
        int value;
        return m.Success && int.TryParse(m.Groups["v"].Value, out value) ? value : defaultValue;
    }

    public static double GetDouble(string json, string key, double defaultValue)
    {
        if (string.IsNullOrEmpty(json))
        {
            return defaultValue;
        }
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>-?\\d+(\\.\\d+)?)");
        double value;
        return m.Success && double.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out value) ? value : defaultValue;
    }

    public static bool GetBool(string json, string key, bool defaultValue)
    {
        if (string.IsNullOrEmpty(json))
        {
            return defaultValue;
        }
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)", RegexOptions.IgnoreCase);
        return m.Success ? string.Equals(m.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase) : defaultValue;
    }
}

[Serializable]
public class TKWebPanelConfig
{
    public bool enabled = true;
    // 0 = automatique (port du jeu + 4)
    public int port = 0;
    // Mot de passe du panel (généré automatiquement si vide)
    public string password = "";
    // Cœurs alloués à l'instance (pour le % CPU du monitoring)
    public int allocatedCores = 3;
    // Hôte affiché dans l'URL en console (vide = IP publique auto-détectée)
    public string publicHost = "";
    // Bannière console en vert ANSI (désactiver si la console affiche des caractères bizarres)
    public bool ansiColors = true;
    // URL publique complète affichée dans la bannière (ex. https://nova-life.teamkit.fr/vizu/)
    public string publicUrl = "";
    // Conservation des historiques chat + journal d'activité (jours, min 92 = 3 mois)
    public int logRetentionDays = 92;
    // Intervalle de la sauvegarde automatique de la base (heures)
    public int backupIntervalHours = 6;

    public static string ToJson(TKWebPanelConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"port\": " + c.port + ",");
        sb.AppendLine("  \"password\": " + Json.Str(c.password) + ",");
        sb.AppendLine("  \"allocatedCores\": " + c.allocatedCores + ",");
        sb.AppendLine("  \"publicHost\": " + Json.Str(c.publicHost) + ",");
        sb.AppendLine("  \"ansiColors\": " + (c.ansiColors ? "true" : "false") + ",");
        sb.AppendLine("  \"publicUrl\": " + Json.Str(c.publicUrl) + ",");
        sb.AppendLine("  \"logRetentionDays\": " + c.logRetentionDays + ",");
        sb.AppendLine("  \"backupIntervalHours\": " + c.backupIntervalHours);
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static TKWebPanelConfig FromJson(string json)
    {
        TKWebPanelConfig c = new TKWebPanelConfig();
        if (string.IsNullOrEmpty(json))
        {
            return c;
        }
        c.enabled = Json.GetBool(json, "enabled", c.enabled);
        c.port = Json.GetInt(json, "port", c.port);
        c.password = Json.GetString(json, "password", c.password);
        c.allocatedCores = Json.GetInt(json, "allocatedCores", c.allocatedCores);
        c.publicHost = Json.GetString(json, "publicHost", c.publicHost);
        c.ansiColors = Json.GetBool(json, "ansiColors", c.ansiColors);
        c.publicUrl = Json.GetString(json, "publicUrl", c.publicUrl);
        c.logRetentionDays = Json.GetInt(json, "logRetentionDays", c.logRetentionDays);
        if (c.logRetentionDays < 92) c.logRetentionDays = 92;
        c.backupIntervalHours = Json.GetInt(json, "backupIntervalHours", c.backupIntervalHours);
        if (c.backupIntervalHours < 1) c.backupIntervalHours = 1;
        if (c.port != 0 && (c.port < 1024 || c.port > 65535))
        {
            c.port = 0;
        }
        return c;
    }
}
