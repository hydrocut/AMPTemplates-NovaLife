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
/// TKWebPanel v1.4 — TeamKit.fr
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
            Debug.Log("[TKWEB] Plugin TKWebPanel v1.4 désactivé par config");
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
            Debug.Log("[TKWEB] Plugin TKWebPanel v1.4 initialisé — panel sur le port " + port);
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
            case "/api/accheck":
                return ApiAntiCheat();
            case "/api/history":
                return ApiHistory();
            case "/api/offlineinv":
                return ApiOfflineInventory(ctx.Request.QueryString["characterId"]);
            case "/api/offlineremoveitem":
                return ApiOfflineRemoveItem(body);
            case "/api/offlinevehicles":
                return ApiOfflineVehicles(ctx.Request.QueryString["characterId"]);
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
            foreach (LifeVehicle v in Nova.v.vehicles)
            {
                if (v == null || v.permissions == null)
                {
                    continue;
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
            foreach (LifeVehicle v in Nova.v.vehicles)
            {
                if (v == null || v.permissions == null)
                {
                    continue;
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

    public static string ToJson(TKWebPanelConfig c)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"enabled\": " + (c.enabled ? "true" : "false") + ",");
        sb.AppendLine("  \"port\": " + c.port + ",");
        sb.AppendLine("  \"password\": " + Json.Str(c.password) + ",");
        sb.AppendLine("  \"allocatedCores\": " + c.allocatedCores + ",");
        sb.AppendLine("  \"publicHost\": " + Json.Str(c.publicHost) + ",");
        sb.AppendLine("  \"ansiColors\": " + (c.ansiColors ? "true" : "false"));
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
        if (c.port != 0 && (c.port < 1024 || c.port > 65535))
        {
            c.port = 0;
        }
        return c;
    }
}
