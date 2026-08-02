using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using UnityEngine;

namespace TeamKitIntro;

public class TeamKitIntroPlugin : Plugin
{
	private const string Version = "1.0.2";

	private string pluginDirectoryPath;

	private string configPath;

	private string seenPlayersPath;

	private IntroConfig config;

	private SeenPlayersDatabase seenPlayers;

	private SChatCommand introCommand;

	private SChatCommand introResetCommand;

	public TeamKitIntroPlugin(IGameAPI api)
		: base(api)
	{
	}

	public override void OnPluginInit()
	{
		base.OnPluginInit();
		try
		{
			InitFiles();
			RegisterCommands();
			Debug.Log("[TeamKitIntro v1.0.2] success : initialisé");
			Debug.Log("[TeamKitIntro] success : commande /intro enregistrée");
		}
		catch (Exception ex)
		{
			Debug.LogError("[TeamKitIntro] erreur initialisation : " + ex);
		}
	}

	public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
	{
		base.OnPlayerSpawnCharacter(player, conn, character);
		try
		{
			if (player != null && config != null && config.enabled && config.autoOpenOnSpawn)
			{
				string playerSteamId = GetPlayerSteamId(player);
				if (!config.showOnlyFirstJoin || !seenPlayers.HasSeenIntro(playerSteamId))
				{
					Debug.Log("[TeamKitIntro] ouverture automatique intro pour " + GetPlayerName(player) + " (" + playerSteamId + ")");
					ShowIntroPanel(player, manualOpen: false);
				}
			}
		}
		catch (Exception ex)
		{
			Debug.LogError("[TeamKitIntro] erreur OnPlayerSpawnCharacter : " + ex);
		}
	}

	private void RegisterCommands()
	{
		introCommand = new SChatCommand("/intro", "Affiche l'accueil TeamKit.", "/intro", delegate(Player player, string[] args)
		{
			ShowIntroPanel(player, manualOpen: true);
		});
		introCommand.Register();
		if (config.enableResetCommand)
		{
			introResetCommand = new SChatCommand("/introreset", "Réinitialise ton intro TeamKit.", "/introreset", delegate(Player player, string[] args)
			{
				string playerSteamId = GetPlayerSteamId(player);
				seenPlayers.Remove(playerSteamId);
				SaveSeenPlayers();
				player.SendText("[TeamKitIntro] Ton intro a été réinitialisée. Tape /intro ou reconnecte-toi.");
				Debug.Log("[TeamKitIntro] reset intro pour " + GetPlayerName(player) + " (" + playerSteamId + ")");
			});
			introResetCommand.Register();
			Debug.Log("[TeamKitIntro] success : commande /introreset enregistrée");
		}
	}

	private void ShowIntroPanel(Player player, bool manualOpen)
	{
		if (player != null)
		{
			string text = BuildIntroText(player, manualOpen);
			UIPanel panel = new UIPanel(config.title, UIPanel.PanelType.Text).SetText(text).AddButton(ShortButton(config.enterButtonText, "Entrer"), delegate(UIPanel ui)
			{
				MarkIntroSeen(player);
				player.ClosePanel(ui);
				player.SendText(config.enterChatMessage);
			}).AddButton(ShortButton(config.rulesButtonText, "Regles"), delegate(UIPanel ui)
			{
				player.ClosePanel(ui);
				ShowRulesPanel(player);
			});
			player.ShowPanelUI(panel);
		}
	}

	private void ShowRulesPanel(Player player)
	{
		UIPanel panel = new UIPanel(config.rulesTitle, UIPanel.PanelType.Text).SetText(WrapText(config.rulesText, 36)).AddButton("Retour", delegate(UIPanel ui)
		{
			player.ClosePanel(ui);
			ShowIntroPanel(player, manualOpen: true);
		}).AddButton(ShortButton(config.closeButtonText, "Fermer"), delegate(UIPanel ui)
		{
			player.ClosePanel(ui);
		});
		player.ShowPanelUI(panel);
	}

	private string BuildIntroText(Player player, bool manualOpen)
	{
		string text = Shorten(GetPlayerName(player), 18);
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(WrapText("Bienvenue " + text + " !", 30));
		stringBuilder.AppendLine(WrapText(config.welcomeText, 30));
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("Site : " + Shorten(config.website, 28));
		stringBuilder.AppendLine("Discord : " + Shorten(config.discord, 28));
		stringBuilder.AppendLine();
		stringBuilder.AppendLine(manualOpen ? "Ouverture manuelle." : "Clique sur Entrer.");
		return stringBuilder.ToString();
	}

	private static string ShortButton(string value, string fallback)
	{
		if (string.IsNullOrEmpty(value))
		{
			value = fallback;
		}
		value = value.Trim();
		if (value.Equals("Entrer en ville", StringComparison.OrdinalIgnoreCase))
		{
			return "Entrer";
		}
		if (value.Equals("Règlement", StringComparison.OrdinalIgnoreCase) || value.Equals("Reglement", StringComparison.OrdinalIgnoreCase))
		{
			return "Regles";
		}
		return Shorten(value, 10);
	}

	private static string Shorten(string value, int maxLength)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "Joueur";
		}
		value = value.Trim();
		if (value.Length <= maxLength)
		{
			return value;
		}
		return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
	}

	private static string WrapText(string text, int maxLineLength)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		text = text.Replace("\r", "");
		string[] array = text.Split(new char[1] { '\n' });
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].Trim();
			if (text2.Length == 0)
			{
				stringBuilder.AppendLine();
				continue;
			}
			string[] array2 = text2.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			int num = 0;
			foreach (string text3 in array2)
			{
				if (num > 0 && num + 1 + text3.Length > maxLineLength)
				{
					stringBuilder.AppendLine();
					num = 0;
				}
				if (num > 0)
				{
					stringBuilder.Append(' ');
					num++;
				}
				stringBuilder.Append(text3);
				num += text3.Length;
			}
			if (i < array.Length - 1)
			{
				stringBuilder.AppendLine();
			}
		}
		return stringBuilder.ToString();
	}

	private void MarkIntroSeen(Player player)
	{
		string playerSteamId = GetPlayerSteamId(player);
		string playerName = GetPlayerName(player);
		seenPlayers.MarkSeen(playerSteamId, playerName);
		SaveSeenPlayers();
		Debug.Log("[TeamKitIntro] intro validée par " + playerName + " (" + playerSteamId + ")");
	}

	private void InitFiles()
	{
		pluginDirectoryPath = Path.Combine(pluginsPath, "TeamKitIntro");
		configPath = Path.Combine(pluginDirectoryPath, "config.json");
		seenPlayersPath = Path.Combine(pluginDirectoryPath, "seen_players.json");
		if (!Directory.Exists(pluginDirectoryPath))
		{
			Directory.CreateDirectory(pluginDirectoryPath);
		}
		LoadOrCreateConfig();
		LoadOrCreateSeenPlayers();
	}

	private void LoadOrCreateConfig()
	{
		if (!File.Exists(configPath))
		{
			config = IntroConfig.CreateDefault();
			File.WriteAllText(configPath, SimpleJsonCompat.ToJson(config));
			Debug.Log("[TeamKitIntro] config.json créé : " + configPath);
			return;
		}
		string json = File.ReadAllText(configPath);
		config = SimpleJsonCompat.FromJsonConfig(json);
		if (config == null)
		{
			config = IntroConfig.CreateDefault();
			File.WriteAllText(configPath, SimpleJsonCompat.ToJson(config));
			Debug.LogWarning("[TeamKitIntro] config.json invalide, config par défaut recréée.");
		}
	}

	private void LoadOrCreateSeenPlayers()
	{
		if (!File.Exists(seenPlayersPath))
		{
			seenPlayers = new SeenPlayersDatabase();
			SaveSeenPlayers();
			Debug.Log("[TeamKitIntro] seen_players.json créé : " + seenPlayersPath);
			return;
		}
		string json = File.ReadAllText(seenPlayersPath);
		seenPlayers = SimpleJsonCompat.FromJsonSeenPlayers(json);
		if (seenPlayers == null)
		{
			seenPlayers = new SeenPlayersDatabase();
		}
		if (seenPlayers.players == null)
		{
			seenPlayers.players = new List<SeenPlayerRecord>();
		}
	}

	private void SaveSeenPlayers()
	{
		File.WriteAllText(seenPlayersPath, SimpleJsonCompat.ToJson(seenPlayers));
	}

	private string GetPlayerSteamId(Player player)
	{
		try
		{
			return player.steamId.ToString();
		}
		catch
		{
			return "unknown";
		}
	}

	private string GetPlayerName(Player player)
	{
		try
		{
			if (!string.IsNullOrEmpty(player.FullName))
			{
				return player.FullName;
			}
		}
		catch
		{
		}
		return "Joueur";
	}
}
