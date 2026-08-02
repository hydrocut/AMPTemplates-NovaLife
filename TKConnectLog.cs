using System.Collections.Generic;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using UnityEngine;

/// <summary>
/// TKConnectLog v1.2 — TeamKit.fr
///
/// 1) Écrit dans la console serveur des lignes normalisées à la connexion
///    et à la déconnexion des joueurs, pour qu'AMP affiche les pseudos
///    et suive les entrées/sorties :
///      [TKLOG] JOIN pseudo="PseudoSteam" steamid=76561198000000000
///      [TKLOG] LEAVE pseudo="PseudoSteam" steamid=76561198000000000
///
/// 2) Annonce en jeu les arrivées/départs et souhaite la bienvenue
///    aux joueurs avec le message TeamKit.
///
/// v1.2 :
///  - Déconnexions détectées via LifeServer.OnPlayerDisconnectEvent
///    (le hook plugin OnPlayerDisconnect n'est jamais appelé par le jeu)
///  - Pseudo Steam vide au spawn : repli sur le nom RP puis le SteamID
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

    private bool disconnectHooked;

    public TKConnectLog(IGameAPI api) : base(api)
    {
    }

    public override void OnPluginInit()
    {
        base.OnPluginInit();
        HookDisconnectEvent();
        Debug.Log("[TKLOG] Plugin TKConnectLog v1.2 initialisé");
    }

    // Le jeu n'appelle jamais le hook plugin OnPlayerDisconnect : on se branche
    // directement sur l'événement public du serveur.
    private void HookDisconnectEvent()
    {
        if (disconnectHooked)
        {
            return;
        }
        try
        {
            if (Nova.server != null)
            {
                Nova.server.OnPlayerDisconnectEvent += HandleDisconnect;
                disconnectHooked = true;
                Debug.Log("[TKLOG] Événement de déconnexion branché");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[TKLOG] Impossible de brancher l'événement de déconnexion : " + ex.Message);
        }
    }

    private static string ResolvePseudo(Player player, Characters character)
    {
        // steamUsername peut être vide au moment du spawn (résolution Steam asynchrone)
        string pseudo = player.steamUsername;
        if (string.IsNullOrWhiteSpace(pseudo))
        {
            try { pseudo = player.FullName; } catch { }
        }
        if (string.IsNullOrWhiteSpace(pseudo) && character != null)
        {
            try { pseudo = (character.Firstname + " " + character.Lastname).Trim(); } catch { }
        }
        if (string.IsNullOrWhiteSpace(pseudo))
        {
            pseudo = "Joueur " + player.steamId;
        }
        return pseudo.Trim();
    }

    public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
    {
        base.OnPlayerSpawnCharacter(player, conn, character);
        HookDisconnectEvent();

        string pseudo = ResolvePseudo(player, character);
        string logLine = $"pseudo=\"{pseudo}\" steamid={player.steamId}";

        // Ne traite le JOIN qu'une fois par connexion (pas à chaque changement de personnage)
        if (!connectedPlayers.ContainsKey(conn.connectionId))
        {
            connectedPlayers[conn.connectionId] = new PlayerInfo
            {
                pseudo = pseudo,
                logLine = logLine
            };

            // Ligne console pour AMP
            Debug.Log($"[TKLOG] JOIN {logLine}");

            // Annonce à tous les joueurs
            Nova.server.SendMessageToAll(string.Format(JoinBroadcast, pseudo));

            // Message de bienvenue au joueur qui arrive
            player.SendText(WelcomeLine1);
            player.SendText(WelcomeLine2);
        }
        else
        {
            connectedPlayers[conn.connectionId].pseudo = pseudo;
            connectedPlayers[conn.connectionId].logLine = logLine;
        }
    }

    // Garde le hook officiel au cas où le jeu le câblerait un jour :
    // HandleDisconnect retire le joueur du dictionnaire, donc pas de doublon possible.
    public override void OnPlayerDisconnect(NetworkConnection conn)
    {
        base.OnPlayerDisconnect(conn);
        HandleDisconnect(conn);
    }

    private void HandleDisconnect(NetworkConnection conn)
    {
        if (conn == null)
        {
            return;
        }

        if (connectedPlayers.TryGetValue(conn.connectionId, out PlayerInfo info))
        {
            connectedPlayers.Remove(conn.connectionId);

            // Ligne console pour AMP
            Debug.Log($"[TKLOG] LEAVE {info.logLine}");

            // Annonce à tous les joueurs restants
            try
            {
                Nova.server.SendMessageToAll(string.Format(LeaveBroadcast, info.pseudo));
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[TKLOG] Erreur annonce départ : " + ex.Message);
            }
        }
    }
}
