// Copied from LockPick Helper 2.1.0 (github.com/tslaceo/G1LockPickHelper, commit 7991521, MIT License, (c) 2026 tslaceo)
// with the prefix LPH renamed to DsLp. Changes are marked "DsLp:".
// DsLp: the single source of the path to the open lock (v23, user decision 14.11.2026: the helper replaces our own
// pinned-target logic). DsLp::SameHoles lives in DsLp_Game.as (same code), so it is not repeated here.
// DsLp (v24, user decision 14.11.2026): ONE path, kept. A plan is the list of its A/D moves (plate + direction), fixed
// when it is worked out. A W/S that is not the hinted key no longer replans (the helper did, and the solver's choice among
// equally short paths follows the cursor, so the target plate jumped above / below the cursor): only the W/S route from
// the cursor to the plan's next move is rebuilt (Renavigate, Route grows). A new plan (the shortest from the state then)
// is worked out only when the lock does something else: an A/D that is not the plan's next move, R, a strain, the game
// disagreeing with the expected holes, an unknown cursor.

// The tracker: follows the player through the hint (tracker.lua of 1.2.0, ported by meaning).
//
// The game shows the plates' holes but not which plate the pick is on, so the cursor is followed from the W/S presses as
// an interval Lo..Hi of possible plates (Lo == Hi when it is known). A press updates the expected holes at once; the
// settled holes read from the game confirm them. When the game disagrees, the tracker takes the game's holes and infers
// the cursor from the plate that moved; when that is impossible the new hint starts with enough S presses to park the
// cursor on plate 0 (S does not wrap), so a hint never relies on a guessed cursor. A strained move may break the pick,
// which resets the lock and puts the cursor on plate 0, so strains and a return to the starting holes forget the cursor.
//
// New in 2.0: the game's own highlight shows the selected plate. Observe is given it, and when the cursor is not known
// and no press is waiting for the game, the highlight is the cursor (no parking).
//
// Plates and the cursor are 0-based here (1.x counted from 1); the hint is Keys[Index] and on.

enum EDsLp_Status
{
	Ok, // the hint is Keys, its current key Keys[Index] (Index == Keys.Num(): the keys are used up, the game has not shown the open lock yet)
	Solved, // the game shows every hole at 4
	NoSafePath, // no sequence of keys opens the lock from here without a strain
	BadState // the holes do not fit the lock (or the solver is not ready)
}

// A W/S/A/D that was pressed while a hint was shown, and whether it was the key the hint showed.
struct FDsLp_Press
{
	EDsLp_Key Key;
	bool bOk = false;
}

// What the hint shows. Plan grows by one every time the keys are worked out again (1.x: the keys list was a new table
// then); it only ever grows, also across Init calls of one tracker.
struct FDsLp_TrackerView
{
	EDsLp_Status Status = EDsLp_Status::BadState;
	TArray<EDsLp_Key> Keys; // empty unless Status is Ok
	int Index = 0; // the current key is Keys[Index]
	TArray<FDsLp_Press> History; // the last DsLp::TrackerHistoryMax presses (R clears it)
	int Misses = 0; // presses that were not the hinted key (only grows)
	int Pressed = 0; // presses added to the history (only grows; the history itself is capped)
	int Plan = 0;
	int Route = 0; // DsLp: grows when the W/S route to the plan's next move is rebuilt (the plan itself is the same)
}

// The numbers of the view without its copies of the keys and the history (View() copies both, up to 512 presses).
// Whatever View() returns changes only together with one of these: the keys only on a replan (Plan grows), the history
// with Pressed or on R (which replans), the status on a replan or when the game shows the open lock. A caller that looks
// every frame compares these and calls View() only when they changed.
struct FDsLp_TrackerSummary
{
	EDsLp_Status Status = EDsLp_Status::BadState;
	int Plan = 0;
	int Index = 0;
	int HistoryNum = 0; // History.Num() of the view
	int Pressed = 0;
	int Misses = 0;
	int Route = 0; // DsLp
}

namespace DsLp
{
	const int TrackerHistoryMax = 512; // presses kept for the hint's "done" part
	const int64 NeverMs = -1000000000000; // a time long before any press

	// DsLp: SameHoles is in DsLp_Game.as.

	// True when there is a reading and every hole in it is at 4.
	bool AllAtGoal(const TArray<int>& Holes)
	{
		if (Holes.Num() == 0)
			return false;
		for (int Hole : Holes)
		{
			if (Hole != HoleGoal)
				return false;
		}
		return true;
	}
}

// The expected holes after an A/D, and how many presses of Since there were by then.
struct FDsLp_TrailStep
{
	TArray<int> Holes;
	int Count = 0;
}

class UDsLp_Tracker : UObject
{
	UPROPERTY()
	UDsLp_Solver Solver;

	int PlateCount = 0;
	int GraceWindowMs = 1200; // how long a press may stay invisible before the game is believed
	TArray<int> StartHoles; // the holes the lock returns to when the pick breaks or on R
	TArray<int> Expected; // the holes expected after every press so far
	TArray<int> Seen; // the last holes confirmed by the game
	TArray<EDsLp_Key> Since; // the presses after Seen was confirmed
	TArray<FDsLp_TrailStep> Trail; // the expected holes after each A/D since Seen
	int64 LastMoveMs = 0;
	bool bDone = false;
	TArray<FDsLp_Press> History;
	int Misses = 0;
	int Pressed = 0;
	int Lo = 0; // the cursor is on one of the plates Lo..Hi
	int Hi = 0;
	TArray<EDsLp_Key> Keys;
	int Index = 0;
	EDsLp_Status Status = EDsLp_Status::BadState;
	int Plan = 0;
	// DsLp: the plan's A/D moves in order (plate, key) and the next one not yet made; Route counts rebuilt W/S routes.
	TArray<int> MovePlates;
	TArray<EDsLp_Key> MoveKeys;
	int MoveIdx = 0;
	int Route = 0;

	// Starts following a lock: Prepared is the lock's ready solver, Holes the settled holes when the minigame became
	// playable, bCursorKnown false when W/S may have been pressed before (the cursor is then not trusted), GraceMs how
	// long a press may stay invisible (DsLp::GraceMs). Calling it again starts afresh (Plan keeps growing).
	void Init(UDsLp_Solver Prepared, const TArray<int>& Holes, bool bCursorKnown, int GraceMs)
	{
		Solver = Prepared;
		PlateCount = 0;
		if (Prepared != nullptr)
			PlateCount = Prepared.Plates();
		GraceWindowMs = GraceMs;
		StartHoles = Holes;
		Expected = Holes;
		Seen = Holes;
		Since.Reset();
		Trail.Reset();
		LastMoveMs = DsLp::NeverMs;
		bDone = false;
		History.Reset();
		Misses = 0;
		Pressed = 0;
		Lo = 0;
		Hi = 0;
		if (!bCursorKnown)
			Hi = LastPlate();
		Replan();
	}

	// A press of the player or of the autosolver; Now is the time of the press (DsLp::NowMs()).
	void Key(EDsLp_Key Key, int64 Now)
	{
		if (bDone)
			return;
		if (Key != EDsLp_Key::R && Status == EDsLp_Status::Ok && Index < Keys.Num())
		{
			FDsLp_Press Press;
			Press.Key = Key;
			Press.bOk = Key == Keys[Index];
			History.Add(Press);
			if (History.Num() > DsLp::TrackerHistoryMax)
				History.RemoveAt(0);
			Pressed++;
			if (!Press.bOk)
				Misses++;
		}
		Since.Add(Key);

		if (Key == EDsLp_Key::W || Key == EDsLp_Key::S)
		{
			Shift(Key);
		}
		else if (Key == EDsLp_Key::A || Key == EDsLp_Key::D)
		{
			LastMoveMs = Now;
			if (Lo == Hi)
			{
				TArray<int> Moved;
				int MovedCursor = Lo;
				if (Solver != nullptr && Solver.Apply(Expected, Lo, Key, Moved, MovedCursor))
				{
					Expected = Moved;
					FDsLp_TrailStep Step;
					Step.Holes = Moved;
					Step.Count = Since.Num();
					Trail.Add(Step);
				}
				else
				{
					// A strain: the pick may break and reset the cursor.
					Lo = 0;
					Hi = LastPlate();
				}
			}
		}
		else
		{
			// R: the lock goes back to the start and the cursor to plate 0 (the game animates it, pending counts it).
			LastMoveMs = Now;
			Lo = 0;
			Hi = LastPlate();
			Expected = StartHoles;
			Trail.Reset();
			History.Reset();
			Replan();
			return;
		}

		if (Index < Keys.Num() && Keys[Index] == Key)
		{
			if (Key == EDsLp_Key::A || Key == EDsLp_Key::D)
				MoveIdx++;
			Index++;
		}
		else if ((Key == EDsLp_Key::W || Key == EDsLp_Key::S) && Status == EDsLp_Status::Ok && Lo == Hi && MoveIdx < MovePlates.Num())
			Renavigate(); // DsLp: the cursor went elsewhere; the plan stays, only the way back to its next move changes
		else
			Replan();
	}

	// DsLp: Keys = from the known cursor (Lo) the W/S to each remaining planned move's plate, then that move.
	void Renavigate()
	{
		Route++;
		Index = 0;
		Keys.Reset();
		int At = Lo;
		for (int m = MoveIdx; m < MovePlates.Num(); m++)
		{
			while (At < MovePlates[m])
			{
				Keys.Add(EDsLp_Key::W);
				At++;
			}
			while (At > MovePlates[m])
			{
				Keys.Add(EDsLp_Key::S);
				At--;
			}
			Keys.Add(MoveKeys[m]);
		}
	}

	// The game's reading: the settled holes of the plates, the plate the game highlights (0..plates - 1; -1: no reading
	// of it), and the time of the reading. If the cursor is not known and no press was waiting for the game when the
	// reading came, the highlight is the cursor from now on and the hint is worked out again without parking. (A highlight
	// is believed only in that case: with a known cursor the presses are the better witness, with a press in flight the
	// highlight may not show it yet.)
	void Observe(const TArray<int>& Holes, int Cursor, int64 Now)
	{
		bool bIdle = Since.Num() == 0;
		ReadHoles(Holes, Now);
		if (bDone || !bIdle || Lo == Hi || Cursor < 0 || Cursor >= PlateCount)
			return;
		Lo = Cursor;
		Hi = Cursor;
		Replan();
	}

	// Moves the game has not confirmed yet: A/D and R (R animates the lock back to the start). 0: the plates show every
	// move so far; the autosolver sends the next key only at 0.
	int Pending() const
	{
		if (bDone)
			return 0;
		int Count = 0;
		for (EDsLp_Key Each : Since)
		{
			if (Each == EDsLp_Key::A || Each == EDsLp_Key::D || Each == EDsLp_Key::R)
				Count++;
		}
		return Count;
	}

	FDsLp_TrackerView View() const
	{
		FDsLp_TrackerView Result;
		Result.Status = Status;
		Result.Keys = Keys;
		Result.Index = Index;
		Result.History = History;
		Result.Misses = Misses;
		Result.Pressed = Pressed;
		Result.Plan = Plan;
		Result.Route = Route;
		if (bDone)
		{
			Result.Status = EDsLp_Status::Solved;
			Result.Keys.Reset();
			Result.Index = 0;
		}
		return Result;
	}

	// View() without the copies (FDsLp_TrackerSummary).
	FDsLp_TrackerSummary Summary() const
	{
		FDsLp_TrackerSummary Result;
		Result.Status = Status;
		Result.Plan = Plan;
		Result.Index = Index;
		Result.HistoryNum = History.Num();
		Result.Pressed = Pressed;
		Result.Misses = Misses;
		Result.Route = Route;
		if (bDone)
		{
			Result.Status = EDsLp_Status::Solved;
			Result.Index = 0;
		}
		return Result;
	}

	// The last plate (0 for a lock without plates).
	int LastPlate() const
	{
		return Math::Max(PlateCount - 1, 0);
	}

	// A new hint from the expected holes: Index 0; Ok, NoSafePath or BadState. When the cursor is only known to be among
	// Lo..Hi the hint starts with Hi S presses (the cursor may be on plate Hi and S does not wrap) and then goes on from
	// plate 0.
	void Replan()
	{
		Plan++;
		Index = 0;
		Keys.Reset();
		MovePlates.Reset();
		MoveKeys.Reset();
		MoveIdx = 0;

		int At = 0;
		if (Lo == Hi)
			At = Lo;
		EDsLp_SolveStatus Reason = EDsLp_SolveStatus::BadState;
		TArray<EDsLp_Key> Found;
		if (Solver != nullptr)
			Reason = Solver.Solve(Expected, At, Found);

		if (Reason != EDsLp_SolveStatus::Ok)
		{
			if (Reason == EDsLp_SolveStatus::NoSafePath)
				Status = EDsLp_Status::NoSafePath;
			else
				Status = EDsLp_Status::BadState;
			return;
		}
		if (Lo != Hi)
		{
			for (int i = 0; i < Hi; i++)
				Keys.Add(EDsLp_Key::S);
		}
		for (EDsLp_Key Each : Found)
			Keys.Add(Each);
		Status = EDsLp_Status::Ok;
		// DsLp: the plan's moves, walking the cursor through the keys (a parking start goes from Hi down to 0).
		int Walk = Hi;
		for (EDsLp_Key Each : Keys)
		{
			if (Each == EDsLp_Key::W)
				Walk = Math::Clamp(Walk + 1, 0, LastPlate());
			else if (Each == EDsLp_Key::S)
				Walk = Math::Clamp(Walk - 1, 0, LastPlate());
			else if (Each == EDsLp_Key::A || Each == EDsLp_Key::D)
			{
				MovePlates.Add(Walk);
				MoveKeys.Add(Each);
			}
		}
	}

	// W/S move the cursor interval one plate (it stops at the ends).
	void Shift(EDsLp_Key Key)
	{
		int Step = 1;
		if (Key == EDsLp_Key::S)
			Step = -1;
		Lo = Math::Clamp(Lo + Step, 0, LastPlate());
		Hi = Math::Clamp(Hi + Step, 0, LastPlate());
	}

	// The game's holes are the truth now: nothing is waiting for it any more.
	void Confirm(const TArray<int>& Holes)
	{
		Seen = Holes;
		Since.Reset();
		Trail.Reset();
	}

	// The game disagrees with the expected state: take its holes and infer the cursor from the plate that moved.
	void Resync(const TArray<int>& Holes)
	{
		int Moves = 0;
		TArray<EDsLp_Key> After; // the W/S after the last A/D
		for (EDsLp_Key Each : Since)
		{
			if (Each == EDsLp_Key::A || Each == EDsLp_Key::D)
			{
				Moves++;
				After.Reset();
			}
			else if (Each == EDsLp_Key::R)
			{
				Moves += 2;
			}
			else
			{
				After.Add(Each);
			}
		}

		// One move since the confirmed holes: the cursor was on a plate where A or D gives what the game shows.
		bool bFound = false;
		int Low = 0;
		int High = 0;
		if (Moves == 1 && Solver != nullptr && !DsLp::SameHoles(Holes, StartHoles))
		{
			for (int Plate = 0; Plate < PlateCount; Plate++)
			{
				for (int Side = 0; Side < 2; Side++)
				{
					EDsLp_Key Try = EDsLp_Key::A;
					if (Side == 1)
						Try = EDsLp_Key::D;
					TArray<int> Moved;
					int MovedCursor = Plate;
					if (!Solver.Apply(Seen, Plate, Try, Moved, MovedCursor) || !DsLp::SameHoles(Moved, Holes))
						continue;
					if (bFound)
					{
						Low = Math::Min(Low, Plate);
						High = Math::Max(High, Plate);
					}
					else
					{
						Low = Plate;
						High = Plate;
						bFound = true;
					}
				}
			}
		}

		if (bFound)
		{
			Lo = Low;
			Hi = High;
			for (EDsLp_Key Each : After)
				Shift(Each);
		}
		else
		{
			Lo = 0;
			Hi = LastPlate();
		}
		Expected = Holes;
		Confirm(Holes);
		Replan();
	}

	// The settled holes read from the game against what the presses expect.
	void ReadHoles(const TArray<int>& Holes, int64 Now)
	{
		if (bDone)
			return;
		if (DsLp::AllAtGoal(Holes))
		{
			bDone = true;
			return;
		}
		if (DsLp::SameHoles(Holes, Expected))
		{
			Confirm(Holes);
			return;
		}
		if (DsLp::SameHoles(Holes, Seen))
		{
			if (Now - LastMoveMs < GraceWindowMs)
				return;
			// The presses since Seen did not move the plates: swallowed by the game, or strained. A strain may break
			// the pick (cursor back on plate 0), and if the game dropped a W/S the tracker counted, the cursor is wrong
			// as well, so it is forgotten (parking costs at most plates - 1 S presses).
			Lo = 0;
			Hi = LastPlate();
			Expected = Holes;
			Confirm(Holes);
			Replan();
			return;
		}
		for (int i = 0; i < Trail.Num(); i++)
		{
			if (!DsLp::SameHoles(Holes, Trail[i].Holes))
				continue;
			// The game finished the first i + 1 moves; later presses are still animating.
			Seen = Holes;
			int Count = Trail[i].Count;
			TArray<EDsLp_Key> Rest;
			for (int j = Count; j < Since.Num(); j++)
				Rest.Add(Since[j]);
			Since = Rest;
			TArray<FDsLp_TrailStep> Later;
			for (int j = i + 1; j < Trail.Num(); j++)
			{
				FDsLp_TrailStep Step;
				Step.Holes = Trail[j].Holes;
				Step.Count = Trail[j].Count - Count;
				Later.Add(Step);
			}
			Trail = Later;
			return;
		}
		Resync(Holes);
	}
}
