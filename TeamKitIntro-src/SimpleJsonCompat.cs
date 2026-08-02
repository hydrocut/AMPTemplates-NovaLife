using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamKitIntro;

internal static class SimpleJsonCompat
{
	public static string ToJson(IntroConfig c)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("{");
		AppendBool(stringBuilder, "enabled", c.enabled, comma: true);
		AppendBool(stringBuilder, "autoOpenOnSpawn", c.autoOpenOnSpawn, comma: true);
		AppendBool(stringBuilder, "showOnlyFirstJoin", c.showOnlyFirstJoin, comma: true);
		AppendBool(stringBuilder, "enableResetCommand", c.enableResetCommand, comma: true);
		AppendString(stringBuilder, "title", c.title, comma: true);
		AppendString(stringBuilder, "subtitle", c.subtitle, comma: true);
		AppendString(stringBuilder, "welcomeText", c.welcomeText, comma: true);
		AppendString(stringBuilder, "rulesTitle", c.rulesTitle, comma: true);
		AppendString(stringBuilder, "rulesText", c.rulesText, comma: true);
		AppendString(stringBuilder, "website", c.website, comma: true);
		AppendString(stringBuilder, "discord", c.discord, comma: true);
		AppendString(stringBuilder, "enterButtonText", c.enterButtonText, comma: true);
		AppendString(stringBuilder, "rulesButtonText", c.rulesButtonText, comma: true);
		AppendString(stringBuilder, "closeButtonText", c.closeButtonText, comma: true);
		AppendString(stringBuilder, "enterChatMessage", c.enterChatMessage, comma: false);
		stringBuilder.AppendLine("}");
		return stringBuilder.ToString();
	}

	public static string ToJson(SeenPlayersDatabase db)
	{
		if (db == null)
		{
			db = new SeenPlayersDatabase();
		}
		if (db.players == null)
		{
			db.players = new List<SeenPlayerRecord>();
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("{");
		stringBuilder.AppendLine("  \"players\": [");
		for (int i = 0; i < db.players.Count; i++)
		{
			SeenPlayerRecord seenPlayerRecord = db.players[i];
			if (seenPlayerRecord != null)
			{
				stringBuilder.AppendLine("    {");
				stringBuilder.Append("      ");
				AppendStringInline(stringBuilder, "steamId", seenPlayerRecord.steamId, comma: true);
				stringBuilder.Append("      ");
				AppendStringInline(stringBuilder, "name", seenPlayerRecord.name, comma: true);
				stringBuilder.Append("      ");
				AppendBoolInline(stringBuilder, "seenIntro", seenPlayerRecord.seenIntro, comma: true);
				stringBuilder.Append("      ");
				AppendStringInline(stringBuilder, "firstSeen", seenPlayerRecord.firstSeen, comma: true);
				stringBuilder.Append("      ");
				AppendStringInline(stringBuilder, "lastSeen", seenPlayerRecord.lastSeen, comma: false);
				stringBuilder.Append("    }");
				if (i < db.players.Count - 1)
				{
					stringBuilder.Append(",");
				}
				stringBuilder.AppendLine();
			}
		}
		stringBuilder.AppendLine("  ]");
		stringBuilder.AppendLine("}");
		return stringBuilder.ToString();
	}

	public static IntroConfig FromJsonConfig(string json)
	{
		IntroConfig introConfig = IntroConfig.CreateDefault();
		if (string.IsNullOrEmpty(json))
		{
			return introConfig;
		}
		introConfig.enabled = GetBool(json, "enabled", introConfig.enabled);
		introConfig.autoOpenOnSpawn = GetBool(json, "autoOpenOnSpawn", introConfig.autoOpenOnSpawn);
		introConfig.showOnlyFirstJoin = GetBool(json, "showOnlyFirstJoin", introConfig.showOnlyFirstJoin);
		introConfig.enableResetCommand = GetBool(json, "enableResetCommand", introConfig.enableResetCommand);
		introConfig.title = GetString(json, "title", introConfig.title);
		introConfig.subtitle = GetString(json, "subtitle", introConfig.subtitle);
		introConfig.welcomeText = GetString(json, "welcomeText", introConfig.welcomeText);
		introConfig.rulesTitle = GetString(json, "rulesTitle", introConfig.rulesTitle);
		introConfig.rulesText = GetString(json, "rulesText", introConfig.rulesText);
		introConfig.website = GetString(json, "website", introConfig.website);
		introConfig.discord = GetString(json, "discord", introConfig.discord);
		introConfig.enterButtonText = GetString(json, "enterButtonText", introConfig.enterButtonText);
		introConfig.rulesButtonText = GetString(json, "rulesButtonText", introConfig.rulesButtonText);
		introConfig.closeButtonText = GetString(json, "closeButtonText", introConfig.closeButtonText);
		introConfig.enterChatMessage = GetString(json, "enterChatMessage", introConfig.enterChatMessage);
		return introConfig;
	}

	public static SeenPlayersDatabase FromJsonSeenPlayers(string json)
	{
		SeenPlayersDatabase seenPlayersDatabase = new SeenPlayersDatabase();
		if (string.IsNullOrEmpty(json))
		{
			return seenPlayersDatabase;
		}
		MatchCollection matchCollection = Regex.Matches(json, "\\{[^\\{\\}]*\\}");
		foreach (Match item in matchCollection)
		{
			string value = item.Value;
			if (value.Contains("\"steamId\""))
			{
				SeenPlayerRecord seenPlayerRecord = new SeenPlayerRecord();
				seenPlayerRecord.steamId = GetString(value, "steamId", "");
				seenPlayerRecord.name = GetString(value, "name", "Joueur");
				seenPlayerRecord.seenIntro = GetBool(value, "seenIntro", defaultValue: true);
				seenPlayerRecord.firstSeen = GetString(value, "firstSeen", "");
				seenPlayerRecord.lastSeen = GetString(value, "lastSeen", "");
				if (!string.IsNullOrEmpty(seenPlayerRecord.steamId))
				{
					seenPlayersDatabase.players.Add(seenPlayerRecord);
				}
			}
		}
		return seenPlayersDatabase;
	}

	private static void AppendString(StringBuilder sb, string key, string value, bool comma)
	{
		sb.Append("  ");
		AppendStringInline(sb, key, value, comma);
	}

	private static void AppendBool(StringBuilder sb, string key, bool value, bool comma)
	{
		sb.Append("  ");
		AppendBoolInline(sb, key, value, comma);
	}

	private static void AppendStringInline(StringBuilder sb, string key, string value, bool comma)
	{
		sb.Append("\"").Append(Escape(key)).Append("\": \"")
			.Append(Escape(value))
			.Append("\"");
		if (comma)
		{
			sb.Append(",");
		}
		sb.AppendLine();
	}

	private static void AppendBoolInline(StringBuilder sb, string key, bool value, bool comma)
	{
		sb.Append("\"").Append(Escape(key)).Append("\": ")
			.Append(value ? "true" : "false");
		if (comma)
		{
			sb.Append(",");
		}
		sb.AppendLine();
	}

	private static string GetString(string json, string key, string defaultValue)
	{
		Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"");
		if (!match.Success)
		{
			return defaultValue;
		}
		return Unescape(match.Groups["v"].Value);
	}

	private static bool GetBool(string json, string key, bool defaultValue)
	{
		Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<v>true|false)", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return defaultValue;
		}
		return string.Equals(match.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase);
	}

	private static string Escape(string value)
	{
		if (value == null)
		{
			return "";
		}
		return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r")
			.Replace("\n", "\\n")
			.Replace("\t", "\\t");
	}

	private static string Unescape(string value)
	{
		if (value == null)
		{
			return "";
		}
		return value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
			.Replace("\\\"", "\"")
			.Replace("\\\\", "\\");
	}
}
