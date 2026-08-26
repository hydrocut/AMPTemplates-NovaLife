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
            Debug.Log("[TKWEB] Plugin TKWebPanel v2.10.1 initialisé — panel sur le port " + port);
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
            case "/api/ghoststats":
            case "/api/heavyareas":
            case "/api/floodbans":
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
        string[] allowed = { "adminProtection", "adminAutoReset", "spamEnabled", "spamKick", "enabled" };
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
        { "TKAntiFlood", new string[] { "enabled:b", "maxAttempts:n", "windowSeconds:n", "banMinutes:n", "whitelist:s" } },
        { "TKAntiCheat", new string[] { "moneyAlertThreshold:n", "maxSpeed:n", "spamThreshold:n", "spamWindowSeconds:n" } }
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
