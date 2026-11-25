// Copied from LockPick Helper 2.1.0 (github.com/tslaceo/G1LockPickHelper, commit 7991521, MIT License, (c) 2026 tslaceo)
// with the prefix LPH renamed to DsLp. Changes are marked "DsLp:".

// The keys of the lockpicking minigame as the mod's solver and hint see them: W/S move the pick between plates, A/D
// push the selected plate, R resets the lock. The letters are what the hint and the log show.

enum EDsLp_Key
{
	W,
	S,
	A,
	D,
	R
}

namespace DsLp
{
	FString KeyLetter(EDsLp_Key Key)
	{
		// An if chain, not a switch: the compiler counts a switch over all the enum's values as complete and
		// calls a return after it unreachable code (which fails the whole script compile).
		if (Key == EDsLp_Key::W)
			return "W";
		if (Key == EDsLp_Key::S)
			return "S";
		if (Key == EDsLp_Key::A)
			return "A";
		if (Key == EDsLp_Key::D)
			return "D";
		if (Key == EDsLp_Key::R)
			return "R";
		return "?";
	}

	// A and D change a plate and so can strain the pick; W, S and R do not.
	bool IsMove(EDsLp_Key Key)
	{
		return Key == EDsLp_Key::A || Key == EDsLp_Key::D;
	}

	// The letters in a row: "WWDSA".
	FString KeysText(const TArray<EDsLp_Key>& Keys)
	{
		FString Result;
		for (EDsLp_Key Key : Keys)
			Result += KeyLetter(Key);
		return Result;
	}
}
