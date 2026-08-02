using System;
using System.Collections.Generic;

namespace TeamKitIntro;

[Serializable]
public class SeenPlayersDatabase
{
	public List<SeenPlayerRecord> players = new List<SeenPlayerRecord>();

	public bool HasSeenIntro(string steamId)
	{
		return Find(steamId)?.seenIntro ?? false;
	}

	public void MarkSeen(string steamId, string name)
	{
		SeenPlayerRecord seenPlayerRecord = Find(steamId);
		string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
		if (seenPlayerRecord == null)
		{
			seenPlayerRecord = new SeenPlayerRecord();
			seenPlayerRecord.steamId = steamId;
			seenPlayerRecord.firstSeen = text;
			players.Add(seenPlayerRecord);
		}
		seenPlayerRecord.name = name;
		seenPlayerRecord.seenIntro = true;
		seenPlayerRecord.lastSeen = text;
	}

	public void Remove(string steamId)
	{
		SeenPlayerRecord seenPlayerRecord = Find(steamId);
		if (seenPlayerRecord != null)
		{
			players.Remove(seenPlayerRecord);
		}
	}

	private SeenPlayerRecord Find(string steamId)
	{
		if (players == null)
		{
			players = new List<SeenPlayerRecord>();
		}
		for (int i = 0; i < players.Count; i++)
		{
			if (players[i] != null && players[i].steamId == steamId)
			{
				return players[i];
			}
		}
		return null;
	}
}
