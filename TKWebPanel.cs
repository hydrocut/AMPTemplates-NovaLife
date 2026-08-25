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
using Life.Network;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// TKWebPanel v1.0 — TeamKit.fr
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

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        LoadConfig();
        if (!config.enabled)
        {
            Debug.Log("[TKWEB] Plugin TKWebPanel v1.0 désactivé par config");
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
            Debug.Log("[TKWEB] Plugin TKWebPanel v1.0 initialisé — panel sur le port " + port);
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
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
        catch
        {
        }
    }

    private bool CheckAuth(HttpListenerContext ctx)
    {
        string provided = ctx.Request.Headers["X-Auth"];
        return !string.IsNullOrEmpty(provided) && SlowEquals(provided, config.password);
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
            if (SlowEquals(pass, config.password))
            {
                return "{\"ok\":true}";
            }
            Thread.Sleep(800); // freine le brute-force
            status = 401;
            return "{\"error\":\"mot de passe incorrect\"}";
        }

        if (!CheckAuth(ctx))
        {
            status = 401;
            return "{\"error\":\"non authentifié\"}";
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
            case "/api/floodbans":
                return ApiFloodBans();
            case "/api/floodunban":
                return ApiFloodUnban(body);
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
            return "{\"ok\":true}";
        });
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

    public static string ToJson(TKWebPanelConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"port\": " + c.port + ",");
        sb.AppendLine("  \"password\": " + Json.Str(c.password) + ",");
        sb.AppendLine("  \"allocatedCores\": " + c.allocatedCores);
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
        if (c.port != 0 && (c.port < 1024 || c.port > 65535))
        {
            c.port = 0;
        }
        return c;
    }
}
