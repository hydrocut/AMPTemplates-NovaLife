using System.Collections.Generic;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using UnityEngine;

/// <summary>
/// TKConnectLog — écrit dans la console serveur des lignes normalisées
/// à la connexion et à la déconnexion des joueurs, pour qu'AMP puisse
/// afficher les pseudos et suivre les entrées/sorties.
///
/// Format des lignes :
///   [TKLOG] JOIN pseudo="PseudoSteam" steamid=76561198000000000
///   [TKLOG] LEAVE pseudo="PseudoSteam" steamid=76561198000000000
/// </summary>
public class TKConnectLog : Plugin
{
    // connectionId -> infos joueur, pour retrouver le pseudo à la déconnexion
    private readonly Dictionary<int, string> connectedPlayers = new Dictionary<int, string>();

    public TKConnectLog(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        Debug.Log("[TKLOG] Plugin TKConnectLog initialisé");
    }

    public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
    {
        base.OnPlayerSpawnCharacter(player, conn, character);

        string info = $"pseudo=\"{player.steamUsername}\" steamid={player.steamId}";

        // Ne logue le JOIN qu'une fois par connexion (pas à chaque changement de personnage)
        if (!connectedPlayers.ContainsKey(conn.connectionId))
        {
            connectedPlayers[conn.connectionId] = info;
            Debug.Log($"[TKLOG] JOIN {info}");
        }
        else
        {
            connectedPlayers[conn.connectionId] = info;
        }
    }

    public override void OnPlayerDisconnect(NetworkConnection conn)
    {
        base.OnPlayerDisconnect(conn);

        if (connectedPlayers.TryGetValue(conn.connectionId, out string info))
        {
            connectedPlayers.Remove(conn.connectionId);
            Debug.Log($"[TKLOG] LEAVE {info}");
        }
    }
}
