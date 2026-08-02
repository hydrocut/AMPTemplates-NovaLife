using System.Collections.Generic;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using UnityEngine;

/// <summary>
/// TKConnectLog v1.1 — TeamKit.fr
///
/// 1) Écrit dans la console serveur des lignes normalisées à la connexion
///    et à la déconnexion des joueurs, pour qu'AMP affiche les pseudos
///    et suive les entrées/sorties :
///      [TKLOG] JOIN pseudo="PseudoSteam" steamid=76561198000000000
///      [TKLOG] LEAVE pseudo="PseudoSteam" steamid=76561198000000000
///
/// 2) Annonce en jeu les arrivées/départs et souhaite la bienvenue
///    aux joueurs avec le message TeamKit.
/// </summary>
public class TKConnectLog : Plugin
{
    // ===== Messages (modifiable ici, puis recompiler) =====
    private const string WelcomeLine1 = "<color=#00f0ff>Bienvenue sur le serveur !</color>";
    private const string WelcomeLine2 = "Serveur hébergé <color=#00ff88>gratuitement</color> par <color=#00f0ff>TeamKit.fr</color>";
    private const string JoinBroadcast = "<color=#00f0ff>{0}</color> <color=#b8b8c8>a rejoint le serveur</color>";
    private const string LeaveBroadcast = "<color=#00f0ff>{0}</color> <color=#b8b8c8>a quitté le serveur</color>";
    // ======================================================

    private class PlayerInfo
    {
        public string pseudo;
        public string logLine;
    }

    // connectionId -> infos joueur, pour retrouver le pseudo à la déconnexion
    private readonly Dictionary<int, PlayerInfo> connectedPlayers = new Dictionary<int, PlayerInfo>();

    public TKConnectLog(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        Debug.Log("[TKLOG] Plugin TKConnectLog v1.1 initialisé");
    }

    public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
    {
        base.OnPlayerSpawnCharacter(player, conn, character);

        string logLine = $"pseudo=\"{player.steamUsername}\" steamid={player.steamId}";

        // Ne traite le JOIN qu'une fois par connexion (pas à chaque changement de personnage)
        if (!connectedPlayers.ContainsKey(conn.connectionId))
        {
            connectedPlayers[conn.connectionId] = new PlayerInfo
            {
                pseudo = player.steamUsername,
                logLine = logLine
            };

            // Ligne console pour AMP
            Debug.Log($"[TKLOG] JOIN {logLine}");

            // Annonce à tous les joueurs
            Nova.server.SendMessageToAll(string.Format(JoinBroadcast, player.steamUsername));

            // Message de bienvenue au joueur qui arrive
            player.SendText(WelcomeLine1);
            player.SendText(WelcomeLine2);
        }
        else
        {
            connectedPlayers[conn.connectionId].logLine = logLine;
        }
    }

    public override void OnPlayerDisconnect(NetworkConnection conn)
    {
        base.OnPlayerDisconnect(conn);

        if (connectedPlayers.TryGetValue(conn.connectionId, out PlayerInfo info))
        {
            connectedPlayers.Remove(conn.connectionId);

            // Ligne console pour AMP
            Debug.Log($"[TKLOG] LEAVE {info.logLine}");

            // Annonce à tous les joueurs restants
            Nova.server.SendMessageToAll(string.Format(LeaveBroadcast, info.pseudo));
        }
    }
}
