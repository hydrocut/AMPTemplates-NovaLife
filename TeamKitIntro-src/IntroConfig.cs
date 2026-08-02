using System;

namespace TeamKitIntro;

[Serializable]
public class IntroConfig
{
	public bool enabled = true;

	public bool autoOpenOnSpawn = true;

	public bool showOnlyFirstJoin = true;

	public bool enableResetCommand = true;

	public string title = "TeamKit.fr";

	public string subtitle = "Serveur RP gratuit";

	public string welcomeText = "Respecte le RP, joue proprement et profite de la ville.";

	public string rulesTitle = "Règlement TeamKit";

	public string rulesText = "1. Respect obligatoire entre joueurs.\n2. Pas de troll, freekill ou abus HRP.\n3. Respecte les scènes RP.\n4. Écoute le staff.\n5. Le serveur est gratuit : aide la communauté à grandir.";

	public string website = "https://www.teamkit.fr";

	public string discord = "https://discord.gg/TON-LIEN";

	public string enterButtonText = "Entrer";

	public string rulesButtonText = "Regles";

	public string closeButtonText = "Fermer";

	public string enterChatMessage = "Bienvenue sur TeamKit.fr | Nova-Life RP.";

	public static IntroConfig CreateDefault()
	{
		return new IntroConfig();
	}
}
