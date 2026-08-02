using System;

namespace TeamKitIntro;

[Serializable]
public class SeenPlayerRecord
{
	public string steamId;

	public string name;

	public bool seenIntro;

	public string firstSeen;

	public string lastSeen;
}
