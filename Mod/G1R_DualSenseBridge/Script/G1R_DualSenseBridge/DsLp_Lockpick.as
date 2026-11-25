// G1R DualSense Bridge — Tactile Lockpicking, stage 1 (vertical slice). See LOCKPICK.md.
//
// An extension of the lockpicking window (W_LockPickUI, one per world, shown again for every minigame):
// - gamepad: L2 / R2 move the selected plate left / right (one move per pull: digital key or analog axis with hysteresis,
//   one shared state per trigger, so both paths never fire twice); L1 / R1 select the plate below / above (user 14.11.2026: L1 = down, R1 = up) (sent as the
//   game's own Up / Down); the whole D-pad does nothing in the minigame. Keyboard keys are not touched (they bubble up).
//   A move is sent with the window's own event (m_OnLeftPressed / m_OnRightPressed.Broadcast) and ONLY for a real press of
//   the player: the game's validation, animation, pick damage and inventory stay the game's.
// - the legend (Button_MoveLeft/Right/Up/Down of W_LockPickUI, Image_Hint) shows the game's own PS L2 / R2 / L1 / R1 icons
//   while the gamepad is the input device;
// - TEMPORARY (testing, user request 14.11.2026): Square / F7 cycles the tactile skill profile auto -> 0 -> 1 -> 2 -> auto,
//   shown by an on-screen label. Only the mod's feedback changes; the lock itself always follows the hero's real skill
//   (the game drops round(LockpickPrecision) connections). Remove with DsLp::TestSkillSwitch = false.
// - the lock's state (plates, selected plate) is read, the solver (LockPick Helper, MIT) predicts which trigger would strain
//   the pick and, at Master skill, which trigger is the next right move; semantic events go to DsBridge (window title):
//     lp:start lock,pl,prec,cut,src   lp:update ph=ready|press|motion|cross|settle|strain|sel|trig|guide|device|skill|alive
//     lp:complete res=open|close   (v16 = Tactile Lockpicking 2.0: press -> observed motion -> crossings -> settle; guide = navigation)
//     lp_break:start state,prev,tx,lives   (v34: the pick breaks - ULockPickAnimInstance.m_LockPickAnimState enters 4 = the start of
//                                            A_Lockpicking_BrokenPick; ~25 ms after the press, the settle "break" comes ~1.9 s later)
//     lp_break:update ph=out,src=bone|timer (v35: the broken tip pick_1 starts moving = A_Lockpicking_BrokenPick_Out)
// - everything is written to Saved/G1R_DualSense_Lockpick.log.
// Nothing is pressed automatically; the solver only informs the feedback.

namespace DsLp
{
    const int ReadyDelayMs = 2000;  // the game ignores presses right after opening (LockPick Helper measurement)
    const int ReadyFrames = 30;
    const int ReadingMs = 50;       // plate readings
    const int LockRetryMs = 100;
    const int LockWaitMs = 2000;
    const int FocusRetryMs = 500;
    const int PromptCheckMs = 250;
    const int AliveMs = 5000;       // heartbeat for DsBridge's trigger watchdog
    const int RecentMs = 3000;      // a reset / strain this recent explains a return to the starting holes
    const int SolverBudgetMs = 4;   // solver preparation per frame
    const float TriggerPress = 0.55;
    const float TriggerRelease = 0.25;
    const FString IconDir = "/Game/UI/Textures/Common/HotkeysIcons/";
    const int ReadingFastMs = 25;   // plate readings while a press waits for / is in motion
    const float MotionThreshold = 0.8; // offset change (units; holes are ~6.2 apart) that counts as observed motion
    const int NoMotionMs = 900;     // a press with no observed motion after this long is closed (no physics played)
    const int MotionLostMs = 3000;  // motion that never settles is closed after this long
    const int GuideDelayMs = 110;   // navigation marker this long after a stable state (spec 90-130 ms)
    const FString SideUp = "R";     // R1 = plate up (W)   (user 14.11.2026)
    const FString SideDown = "L";   // L1 = plate down (S)
    const FString LabelFont = "/Game/UI/Fonts/NotoSerif-Regular_Font.NotoSerif-Regular_Font";
    const int ModVersion = 51;          // v51: spell charge probe (DsB_Charge.as, spellcharge:*), book puzzle probe (DsB_Books.as, book:*); v50: ride probe - possessed pawn with GetMountComponent().GetRiderCharacter() is the mount; v49: ride probe via GetRiderComponent().GetMountCharacter(); v48: ride / rideair probe (DsB_Ride.as), handitem (DsB_Hand.as), touch v11 whistle + map info; v47: oxygen probe (DsB_Air.as, oxygen:start/update/complete); v46: waterfall probe (DsB_Waterfall.as); v45: bow draw progress (bow:update draw/full/alpha); v44: magic observer; lockpicking unchanged
    const bool LoadInMotion = false;    // v36: the load* profiles during plate motion (old Trained) replaced the click under the finger - off for all
    const int OutArmMs = 1900;          // pick_1 is watched from this long after the break (main clip: it stays within ~0.5 local units)
    const int OutTimerMs = 2480;        // no motion seen by then -> ph=out src=timer (v33 fit: Out starts ~2316 ms after state 4)
    const float OutMoveWorld = 1.0;     // pick_1 moved this far (world units, relative to the root socket) -> the tip is being pulled out
    const int BreakAnimFreshMs = 4000;  // a settle "break" this soon after lp_break carries anim=1 (DsBridge then skips the old late lp-break)
    const bool TestSkillSwitch = false;  // v42 (user 15.11.2026, BT approved): off - profile = hero skill, Square / F7 go to the game (true = test switch + label)
    const bool ForceMaster = false;      // (true = every lock gets the Master tactile profile, the switch is off; v25-v30) - v31: switch back on
    const bool ShowSolverStatus = false; // v42 (user 15.11.2026, BT approved): off - no solver text on screen (true = the test label)
    const int GraceMs = 1200;
    const int LifeRefillMs = 1500;       // derived pick life at 0 = the pick broke: a new pick (full life) after this long            // LockPick Helper: how long a move may stay invisible before the game is believed

    // A key of the plan as the player presses it on the pad (and on the keyboard).
    FString KeyHint(EDsLp_Key Key)
    {
        if (Key == EDsLp_Key::W)
            return "R1 / W (пластина выше)";
        if (Key == EDsLp_Key::S)
            return "L1 / S (пластина ниже)";
        if (Key == EDsLp_Key::A)
            return "L2 / A (сдвиг влево)";
        if (Key == EDsLp_Key::D)
            return "R2 / D (сдвиг вправо)";
        return "R";
    }

    FString SkillName(int Level)
    {
        if (Level >= 2)
            return "Мастер";
        if (Level == 1)
            return "Ученик";
        return "Новичок";
    }
}

class UDsLp_LockpickExtension : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/LockPick/W_LockPickUI.W_LockPickUI_C";

    UPROPERTY()
    private UDsLp_Bus Bus;
    UPROPERTY()
    private UDsLp_KeyCatcher Catcher;
    UPROPERTY()
    private UDsLp_Solver Solver;
    UPROPERTY()
    private UDsLp_Tracker Tracker;   // LockPick Helper's tracker: the whole path, followed press by press, replanned on mismatch
    UPROPERTY()
    private UTextBlock SolverLabel;  // TEMPORARY: solver state / "lock cannot be solved"
    UPROPERTY()
    private UTexture2D L2Tex;
    UPROPERTY()
    private UTexture2D R2Tex;
    UPROPERTY()
    private UTexture2D L1Tex;
    UPROPERTY()
    private UTexture2D R1Tex;
    UPROPERTY()
    private UTextBlock TestLabel;
    UPROPERTY()
    private TArray<UImage> SwappedImages;
    UPROPERTY()
    private TArray<UTexture2D> SwappedOriginals;

    // Opt-in read-only animation capture for one broken-pick test. No gameplay/trigger writes.
    UPROPERTY()
    private USkeletalMeshComponent FailureTraceMesh;
    private bool bFailureTrace = false;
    private int FailureTraceState = -999;
    private int64 FailureTraceTryMs = -100000;
    private int64 FailureTraceSampleMs = -100000;
    // v34: the break observer (read-only): the lock pick actor's anim state, one lp_break:start per entry into state 4
    UPROPERTY()
    private USkeletalMeshComponent BreakMesh;
    private int BreakState = -999;
    private bool bBreakArmed = false;     // a state other than 4 was seen since the last break (no event for a state 4 found at open)
    private int64 BreakTryMs = -100000;
    private int64 BreakAnimMs = 0;        // when the last lp_break:start was sent (0 = none in this window)
    private int Breaks = 0;
    private bool bOutPending = false;     // v35: waiting for the broken tip (pick_1) to move
    private FVector BreakP1;              // pick_1 relative to the root socket at the break
    private bool bOpen = false;
    private bool bListening = false;
    private int64 OpenedMs = 0;
    private int Frames = 0;
    private int Session = 0;

    // lock
    private bool bLockSearch = false;
    private int64 LastLockTryMs = 0;
    private bool bLockKnown = false;
    private FName LockName;
    private int Plates = 0;
    private float Prec = 0.0;
    private int RealSkill = 0;   // the hero's skill: max(round(LockpickPrecision), Skill.Lockpicking.* tag)
    private int Removed = 0;
    private bool bSolverPrepared = false;

    // state
    private int64 LastReadMs = 0;
    private bool bReady = false;
    private TArray<int> Candidate;
    private int CandidateCount = 0;
    private TArray<int> Stable;
    private TArray<int> StartHoles;
    private int SelPlate = -1;
    // The path (v23): LockPick Helper's tracker keeps the whole key sequence to the open lock, advances it with every press
    // that follows it and replans when a press deviates or the game shows something else. PlanPlate = the plate of the
    // plan's next A/D (-1: the cursor is not known yet / no plan).
    private bool bEarlyNav = false;   // W/S pressed before the tracker started: its cursor is not trusted
    private int PlanPlate = -1;
    private int SeenPlan = -1;        // the tracker's Plan / Index / Status the feedback was last computed for
    private int SeenIndex = -1;
    private int SeenStatus = -1;
    private int SeenRoute = -1;
    private FString LockNote = "";    // why there is no solver for this minigame (TEMPORARY label)
    private FString LastSolverText = "-";
    private bool bOpenSent = false;
    private FString LastTrig = "";
    private int64 LastAliveMs = 0;
    private int PendingDir = 0;
    private int64 PendingMs = 0;
    private int64 LastResetMs = -100000;
    private int64 LastStrainMs = -100000;
    private int Moves = 0;
    private int Strains = 0;
    private int NavPresses = 0;

    // the pick's life (v30, user 14.11.2026): lightbar + player LEDs show how many strains the current pick still takes
    private int MaxLife = 0;          // LockpickDurability at the start (2 / 4 / 6)
    private int Lives = 0;
    private float LastDurability = 0.0;
    private bool bLifeFromGame = false;  // the game lowered LockpickDurability itself: its value is shown from then on
    private int64 LifeRefillAtMs = 0;
    private FString LastLives = "";

    // a move in flight: press -> observed motion -> plates crossing -> settle (tx = move id within the session)
    private int Tx = 0;
    private bool bTxOpen = false;
    private int64 TxPressMs = 0;
    private int64 TxMotionMs = 0;   // 0 = no motion observed yet
    private int TxDir = 0;
    private bool bTxValid = false;  // the solver's projection exists (and says the move is legal)
    private bool bTxOnRoute = false; // v39: this press was the adaptive route's next move (key and plate) - before the tracker saw it
    private int TxCh = 0;
    private int TxSame = 0;
    private int TxOpp = 0;
    private TArray<int> TxBefore;
    private TArray<int> TxAfter;
    private TArray<float> TxStartDx;
    private TArray<bool> TxCrossed;
    private float MoveMsEma = 300.0; // observed motion duration (session 76: ~300 ms)

    // trigger profiles and navigation
    private FString CurL = "click_soft";
    private FString CurR = "click_soft";
    private FString CurNext = "none";
    private int GuideGen = 0;
    private int64 GuideDueMs = 0;

    // triggers (0 = L2, 1 = R2)
    private bool bLHeld = false;
    private bool bRHeld = false;
    private bool bLDigital = false;
    private bool bRDigital = false;
    private float LAnalog = 0.0;
    private float RAnalog = 0.0;
    private float FirePoint = 0.58;    // analog value that fires a move: just past the trigger click (DsLp_Lockpick.ini)
    private float ReleasePoint = 0.20; // re-arm below this ("Сухой спуск": 0.20 ~ 51/255; DsBridge re-arms the click on ph=trigup)
    private bool bPadFire = false;     // a move is being sent for a real L2/R2 pull (press via=pad), not for a keyboard key
    private bool bAnalogSeen = false;  // analog trigger values arrive (kept for the window's whole life)

    // focus, prompts
    private int64 FocusCheckMs = 0;
    private bool bFocusLost = false;
    private int64 PromptCheckAtMs = 0;
    private int LastInputType = -1;
    private bool bTreeLogged = false;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const
    {
        return true;
    }

    UFUNCTION(BlueprintOverride)
    void Construct()
    {
        Bus = DsLp::Bus();
        ULockPickUIWidget Window = Cast<ULockPickUIWidget>(GetWidget());
        if (Bus == nullptr || Window == nullptr)
            return;
        int64 Now = DsLp::NowMs();
        if (bOpen)
            Line("window shown again without closing");
        bOpen = true;
        Session++;
        OpenedMs = Now;
        Frames = 0;
        FString FailureTraceRequest;
        bFailureTrace = FFileHelper::LoadFileToString(FailureTraceRequest, FPaths::ProjectSavedDir() + "G1R_FailureAnimationTrace.request");
        FailureTraceMesh = nullptr;
        FailureTraceState = -999;
        FailureTraceTryMs = Now - 1000;
        FailureTraceSampleMs = Now - 1000;
        BreakMesh = nullptr;
        BreakState = -999;
        bBreakArmed = false;
        BreakTryMs = Now - 1000;
        BreakAnimMs = 0;
        Breaks = 0;
        bOutPending = false;
        bLockSearch = true;
        LastLockTryMs = Now - DsLp::LockRetryMs;
        bLockKnown = false;
        LockName = NAME_None;
        Plates = 0;
        Prec = 0.0;
        Removed = 0;
        bSolverPrepared = false;
        bReady = false;
        Candidate.Reset();
        CandidateCount = 0;
        Stable.Reset();
        StartHoles.Reset();
        SelPlate = -1;
        Tracker = nullptr;
        bEarlyNav = false;
        PlanPlate = -1;
        SeenPlan = -1;
        SeenIndex = -1;
        SeenStatus = -1;
        SeenRoute = -1;
        LockNote = "";
        LastSolverText = "-";
        bOpenSent = false;
        LastTrig = "";
        LastAliveMs = Now;
        PendingDir = 0;
        LastResetMs = -100000;
        LastStrainMs = -100000;
        Moves = 0;
        Strains = 0;
        NavPresses = 0;
        MaxLife = 0;
        Lives = 0;
        LastDurability = 0.0;
        bLifeFromGame = false;
        LifeRefillAtMs = 0;
        LastLives = "";
        Tx = 0;
        bTxOpen = false;
        TxMotionMs = 0;
        CurL = "click_soft";
        CurR = "click_soft";
        CurNext = "none";
        GuideDueMs = 0;
        ForgetTriggers();
        LastInputType = -1;
        bTreeLogged = false;
        Line(f"=== minigame {Session} opened: difficulty {Window.m_LockDifficulty}, game listens {Window.m_OnLeftPressed.IsBound()}, input type {DsLpGame::InputType()}");

        ReadSettings();
        Listen(Window);
        UCanvasPanel Canvas = Cast<UCanvasPanel>(Window.GetRootWidget());
        if (Canvas == nullptr)
            Line("the window's root is not a canvas: no key catcher, L2/R2 will not work");
        AddCatcher(Window, Canvas);
        AddTestLabel(Window, Canvas);
        UpdateTestLabel();
        AddSolverLabel(Window, Canvas);
        UpdateSolverLabel();
        FocusCheckMs = Now;
        bFocusLost = false;
        if (Catcher != nullptr)
            Line(f"key catcher: keyboard focus {TakeFocus()}");

        PromptCheckAtMs = Now;
        UpdatePrompts(Window);
        FollowLock(Now);
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float InDeltaTime)
    {
        if (!bOpen || Bus == nullptr)
            return;
        ULockPickUIWidget Window = Cast<ULockPickUIWidget>(GetWidget());
        if (Window == nullptr)
            return;
        int64 Now = DsLp::NowMs();
        Frames++;
        if (bFailureTrace)
            FollowFailureAnimationTrace(Now);
        FollowBreakAnim(Now);
        FollowBreakOut(Now);
        FollowLock(Now);
        FollowFocus(Now);
        if (bSolverPrepared && Solver != nullptr && !Solver.IsReady())
        {
            if (Solver.Step(DsLp::SolverBudgetMs))
            {
                Line(f"solver ready after {Now - OpenedMs} ms");
                Recompute("solver", Now);
            }
        }
        FollowLife(Now);
        int ReadEvery = bTxOpen ? DsLp::ReadingFastMs : DsLp::ReadingMs;
        if (Plates > 0 && Now - LastReadMs >= ReadEvery)
        {
            LastReadMs = Now;
            HandleReading(DsLpGame::ReadPlates(Plates), Now);
        }
        FollowPlan(Now);
        if (GuideDueMs > 0 && Now >= GuideDueMs && !bTxOpen)
        {
            GuideDueMs = 0;
            EmitGuide();
        }
        if (Now - PromptCheckAtMs >= DsLp::PromptCheckMs)
        {
            PromptCheckAtMs = Now;
            UpdatePrompts(Window);
        }
        if (Now - LastAliveMs >= DsLp::AliveMs)
        {
            LastAliveMs = Now;
            Emit("update", "ph=alive");
        }
        if (Bus.m_Log != nullptr)
            Bus.m_Log.FlushIfDue();
    }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        if (!bOpen)
            return;
        bOpen = false;
        bLockSearch = false;
        ForgetTriggers();
        int64 Now = DsLp::NowMs();
        Emit("complete", "res=close,moves=" + Moves + ",strains=" + Strains);
        Line(f"=== minigame {Session} closed after {Now - OpenedMs} ms (moves {Moves}, strains {Strains}, opened {bOpenSent})");
        if (Bus != nullptr && Bus.m_Log != nullptr)
            Bus.m_Log.Flush();
    }

    // ---------------- lock, solver ----------------

    private void FollowLock(int64 Now)
    {
        if (!bLockSearch || Now - LastLockTryMs < DsLp::LockRetryMs)
            return;
        LastLockTryMs = Now;
        FName Lock;
        FString Source;
        bool bFound = DsLpGame::FindLock(Lock, Source);
        if (!bFound && Now - OpenedMs < DsLp::LockWaitMs)
            return;
        bLockSearch = false;
        // the hero's skill twice: the attribute the game uses (LockpickPrecision) and the Skill.Lockpicking.* tag behind it;
        // a tag above the attribute means the attribute could not be read -> the tag's value (precision = skill level)
        float AttrPrec = DsLpGame::Precision();
        int TagSkill = DsLpGame::SkillFromTags();
        Prec = AttrPrec;
        if (float(TagSkill) > Prec)
            Prec = float(TagSkill);
        RealSkill = Math::Clamp(Math::RoundToInt(Prec), 0, 2);
        int SkillLevel = RealSkill;
        FString HowChosen = Bus.m_LpSkillOverride >= 0 ? "manual" : "auto";
        Line(f"hero skill: LockpickPrecision {AttrPrec}, Skill.Lockpicking tag {TagSkill} (-1 none) -> {RealSkill}; tactile profile {Skill()} ({HowChosen})");
        UpdateTestLabel();
        StartLife();
        if (!bFound)
        {
            Line(f"no lock found after {Now - OpenedMs} ms: haptics only, no solver");
            LockNote = "Решатель: замок не определён - путь неизвестен";
            Emit("start", "lock=none,pl=0,prec=" + SkillLevel + ",src=none,ver=" + DsLp::ModVersion);
            return;
        }
        LockName = Lock;
        FDsLp_LockGraph Graph;
        int Cut = 0;
        if (!Bus.Db().Find(Lock, DsLp::ConnectionsRemoved(Prec), Graph, Cut))
        {
            Line(f"lock {Lock} ({Source}) is not in the database: haptics only");
            LockNote = "Решатель: замка " + Lock.ToString() + " нет в базе - путь неизвестен";
            Emit("start", "lock=" + Lock.ToString() + ",pl=0,prec=" + SkillLevel + ",src=" + Source + ",known=0,ver=" + DsLp::ModVersion);
            return;
        }
        bLockKnown = true;
        Plates = Graph.Plates;
        Removed = Cut;
        if (Solver == nullptr)
            Solver = Cast<UDsLp_Solver>(NewObject(this, UDsLp_Solver::StaticClass()));
        Solver.Begin(Graph);
        bSolverPrepared = true;
        Line(f"lock {Lock} ({Source}): {Plates} plates, {Graph.Src.Num()} connections after dropping {Cut}; precision {Prec}");
        Emit("start", "lock=" + Lock.ToString() + ",pl=" + Plates + ",prec=" + SkillLevel + ",cut=" + Cut + ",src=" + Source + ",known=1,ver=" + DsLp::ModVersion);
    }

    // The tactile profile: the hero's real skill, or (TEMPORARY test switch) the profile chosen with Square / F7.
    private int Skill()
    {
        if (DsLp::ForceMaster)
            return 2;
        if (DsLp::TestSkillSwitch && Bus != nullptr && Bus.m_LpSkillOverride >= 0)
            return Bus.m_LpSkillOverride;
        return RealSkill;
    }

    // TEMPORARY: Square / F7 -> profile auto -> Novice -> Trained -> Master -> auto.
    void CycleSkill()
    {
        if (!DsLp::TestSkillSwitch || DsLp::ForceMaster || !bOpen || Bus == nullptr)
            return;
        int Next = Bus.m_LpSkillOverride + 1;
        if (Next > 2)
            Next = -1;
        Bus.m_LpSkillOverride = Next;
        FString HowChosen = Next >= 0 ? "manual" : "auto";
        Line(f"TEST skill switch: profile {Skill()} ({HowChosen}), hero {RealSkill}");
        Emit("update", "ph=skill,prof=" + Skill() + ",real=" + RealSkill + ",manual=" + (Next >= 0 ? 1 : 0));
        UpdateTestLabel();
        LastTrig = "";
        Recompute("skill switch", DsLp::NowMs());
    }

    // TEMPORARY on-screen label: the profile in use and how to switch it (top centre of the minigame window).
    private void AddTestLabel(ULockPickUIWidget Window, UCanvasPanel Canvas)
    {
        if (!DsLp::TestSkillSwitch || Canvas == nullptr)
            return;
        if (TestLabel == nullptr)
        {
            TestLabel = Cast<UTextBlock>(Window.ConstructWidget(UTextBlock));
            if (TestLabel == nullptr)
                return;
            FSlateFontInfo Info = TestLabel.Font;
            UFont Font = Cast<UFont>(DsLpGame::LoadAsset(DsLp::LabelFont));
            if (Font != nullptr)
            {
                Info.FontObject = Font;
                Info.TypefaceFontName = n"Default";
            }
            Info.Size = 20;
            TestLabel.SetFont(Info);
            TestLabel.SetColorAndOpacity(FSlateColor(FLinearColor(1.0, 0.84, 0.55, 1.0)));
            TestLabel.SetShadowOffset(FVector2D(1.5, 1.5));
            TestLabel.SetJustification(ETextJustify::Center);
        }
        if (TestLabel.GetParent() == nullptr)
        {
            UCanvasPanelSlot LabelSlot = Canvas.AddChildToCanvas(TestLabel);
            if (LabelSlot != nullptr)
            {
                LabelSlot.SetAnchors(FAnchors(0.5, 0.0, 0.5, 0.0));
                LabelSlot.SetAlignment(FVector2D(0.5, 0.0));
                LabelSlot.SetPosition(FVector2D(0.0, 24.0));
                LabelSlot.SetAutoSize(true);
                LabelSlot.SetZOrder(100);
            }
        }
        TestLabel.SetVisibility(ESlateVisibility::HitTestInvisible);
    }

    private void UpdateTestLabel()
    {
        if (TestLabel == nullptr || Bus == nullptr)
            return;
        if (DsLp::ForceMaster)
        {
            TestLabel.SetText(FText::FromString("ТЕСТ  ·  Тактильный профиль: Мастер (временно для всех)  ·  навык героя: " + DsLp::SkillName(RealSkill)));
            return;
        }
        FString Mode = Bus.m_LpSkillOverride >= 0 ? "выбран вручную" : "как у героя";
        FString Text = "ТЕСТ  ·  Тактильный профиль: " + DsLp::SkillName(Skill()) + " (" + Mode + ")  ·  навык героя: " + DsLp::SkillName(RealSkill)
                     + "\nСменить профиль: Квадрат  /  F7      (авто -> Новичок -> Ученик -> Мастер -> авто)";
        TestLabel.SetText(FText::FromString(Text));
    }

    // ---------------- readings -> semantic events: press -> motion -> cross -> settle ----------------

    private void HandleReading(FDsLp_Reading Reading, int64 Now)
    {
        if (bTxOpen)
            TrackMotion(Reading, Now);
        if (!Reading.bSettled || Reading.Holes.Num() != Plates)
        {
            CandidateCount = 0;
            return;
        }
        // an accepted state = two agreeing settled readings in a row
        if (CandidateCount > 0 && DsLp::SameHoles(Candidate, Reading.Holes))
            CandidateCount++;
        else
        {
            Candidate = Reading.Holes;
            CandidateCount = 1;
        }

        if (Reading.Cursor >= 0 && Reading.Cursor != SelPlate)
        {
            SelPlate = Reading.Cursor;
            if (bReady)
            {
                Emit("update", "ph=sel,cur=" + SelPlate + ",n=" + Plates);
                EmitLinks();
                Recompute("select", Now);
            }
        }

        if (CandidateCount < 2)
            return;

        if (!bReady)
        {
            if (Now - OpenedMs < DsLp::ReadyDelayMs || Frames < DsLp::ReadyFrames)
                return;
            bReady = true;
            Stable = Candidate;
            StartHoles = Candidate;
            Line(f"ready after {Now - OpenedMs} ms: holes {DsLp::HolesText(Stable)}, cursor {SelPlate}");
            Emit("update", "ph=ready,ctr=" + Centered(Stable) + ",n=" + Plates + ",holes=" + DsLp::HolesCode(Stable));
            FeedTracker(Reading, Now);
            Recompute("ready", Now);
            return;
        }

        FeedTracker(Reading, Now);

        if (DsLp::SameHoles(Candidate, Stable))
            return;

        // a new settled layout
        int Changed = 0;
        int CenterIn = 0;
        int CenterOut = 0;
        for (int i = 0; i < Plates; i++)
        {
            if (Candidate[i] == Stable[i])
                continue;
            Changed++;
            if (Candidate[i] == DsLp::HoleGoal)
                CenterIn++;
            if (Stable[i] == DsLp::HoleGoal)
                CenterOut++;
        }
        int Ctr = Centered(Candidate);
        FString Before = DsLp::HolesText(Stable);
        bool bHadTx = bTxOpen;
        TArray<int> PrevStable = Stable;
        Stable = Candidate;
        bool bAtStart = DsLp::SameHoles(Stable, StartHoles);
        FString Dir = "0";
        if (bHadTx)
            Dir = TxDir < 0 ? "L" : "R";
        FString Status = "accepted";
        if (bAtStart && Changed > 0 && Now - LastResetMs < DsLp::RecentMs)
            Status = "reset";
        else if (bAtStart && Changed > 0 && Now - LastStrainMs < DsLp::RecentMs)
            Status = "break";
        int Mismatch = 0;
        int Uncrossed = 0;
        int MotionMs = 0;
        int PressToMotion = 0;
        if (bHadTx)
        {
            if (Status == "accepted" && bTxValid && !DsLp::SameHoles(TxAfter, Stable))
                Mismatch = 1;
            if (bTxValid && TxCrossed.Num() == Plates)
            {
                for (int i = 0; i < Plates; i++)
                {
                    if (TxBefore[i] != TxAfter[i] && !TxCrossed[i])
                        Uncrossed++;
                }
            }
            if (TxMotionMs > 0)
            {
                MotionMs = int(Now - TxMotionMs);
                PressToMotion = int(TxMotionMs - TxPressMs);
            }
        }
        if (Status == "accepted")
        {
            Moves++;
            if (MotionMs > 0)
                MoveMsEma = MoveMsEma * 0.7 + float(MotionMs) * 0.3;
        }
        int TxId = bHadTx ? Tx : 0;
        Line(f"settle {Status} tx {TxId} ({Dir}): {Before} -> {DsLp::HolesText(Stable)}, plates {Changed}, centred {Ctr}/{Plates}, press->motion {PressToMotion} ms, motion {MotionMs} ms, uncrossed {Uncrossed}, mismatch {Mismatch}");
        FString AnimTag = "";
        if (Status == "break")
            AnimTag = (BreakAnimMs > 0 && Now - BreakAnimMs < DsLp::BreakAnimFreshMs) ? ",anim=1" : ",anim=0";
        Emit("update", "ph=settle,status=" + Status + ",tx=" + TxId + ",ch=" + Changed + ",dir=" + Dir + ",ctr=" + Ctr + ",n=" + Plates
                       + ",cin=" + CenterIn + ",cout=" + CenterOut + ",unx=" + Uncrossed + ",mismatch=" + Mismatch + ",pm=" + PressToMotion + ",md=" + MotionMs + AnimTag);
        bTxOpen = false;

        if (Ctr == Plates && !bOpenSent)
        {
            bOpenSent = true;
            Line(f"all plates centred: lock open after {Moves} moves");
            Emit("complete", "res=open,moves=" + Moves + ",strains=" + Strains + ",n=" + Plates);
            return;
        }
        if (Status == "accepted")
            EmitProgress(PrevStable, bHadTx);
        if (Status == "break")
            LifeNewPick("break settled");
        if (Status == "accepted")
            Recompute("settle", Now);
        else
            Recompute(Status, Now);
    }

    // While a press is open: observed motion start (plate offsets leave their press-time values), then per plate the
    // moment it passes half way to its projected hole ("crossing"), reported once per reading as one cluster.
    private void TrackMotion(FDsLp_Reading Reading, int64 Now)
    {
        if (TxMotionMs > 0 && Now - TxMotionMs > DsLp::MotionLostMs)
        {
            CloseTx("motion never settled", Now);
            return;
        }
        if (Reading.Offsets.Num() != Plates || TxStartDx.Num() != Plates)
        {
            if (TxMotionMs == 0 && Now - TxPressMs > DsLp::NoMotionMs)
                CloseTx("no readable motion", Now);
            return;
        }
        FString Side = TxDir < 0 ? "L" : "R";
        if (TxMotionMs == 0)
        {
            bool bMoving = false;
            for (int i = 0; i < Plates; i++)
            {
                if (Math::Abs(Reading.Offsets[i] - TxStartDx[i]) > DsLp::MotionThreshold)
                    bMoving = true;
            }
            if (!bMoving)
            {
                if (Now - TxPressMs > DsLp::NoMotionMs)
                    CloseTx("no motion after the press", Now);
                return;
            }
            TxMotionMs = Now;
            FString Conf = bTxValid ? "observed" : "unknown";
            Line(f"motion start tx {Tx} ({Side}): {Now - TxPressMs} ms after the press, projected plates {TxCh} (same {TxSame}, opposite {TxOpp})");
            Emit("update", "ph=motion,stage=start,tx=" + Tx + ",dir=" + Side + ",ch=" + TxCh + ",same=" + TxSame + ",opp=" + TxOpp
                           + ",est=" + Math::RoundToInt(MoveMsEma) + ",pm=" + int(Now - TxPressMs) + ",conf=" + Conf);
            // Master (v28): the pulled trigger keeps its profile through the motion - switching it to a load profile ~40 ms
            // after the fire point replaced the Bow click under the finger before it could snap (journal play_20261114_173403)
            if (DsLp::LoadInMotion && bTxValid && Skill() == 1)
            {
                FString Load = "load1";
                if (TxOpp > 0)
                    Load = "loadopp";
                else if (TxCh >= 2)
                    Load = "loadn";
                if (TxDir < 0)
                    SetTrig(Load, CurR, Load, "motion");
                else
                    SetTrig(CurL, Load, Load, "motion");
            }
        }
        if (!bTxValid || TxCrossed.Num() != Plates)
            return;
        int NowCrossed = 0;
        int NSame = 0;
        int NOpp = 0;
        int Mask = 0;
        float PSum = 0.0;
        int PCount = 0;
        for (int i = 0; i < Plates; i++)
        {
            if (TxBefore[i] == TxAfter[i])
                continue;
            float From = TxStartDx[i];
            float Span = DsLp::HoleCentre(TxAfter[i]) - From;
            if (Math::Abs(Span) < 0.5)
                continue;
            float Prog = Math::Clamp((Reading.Offsets[i] - From) / Span, 0.0, 1.0);
            PSum += Prog;
            PCount++;
            if (!TxCrossed[i] && Prog >= 0.5)
            {
                TxCrossed[i] = true;
                NowCrossed++;
                Mask = Mask | (1 << i);
                if (TxAfter[i] - TxBefore[i] == TxDir)
                    NSame++;
                else
                    NOpp++;
            }
        }
        if (NowCrossed == 0)
            return;
        int PAvg = PCount > 0 ? Math::RoundToInt(PSum / float(PCount) * 1000.0) : 0;
        Emit("update", "ph=cross,tx=" + Tx + ",dir=" + Side + ",k=" + NowCrossed + ",same=" + NSame + ",opp=" + NOpp + ",mask=" + Mask
                       + ",p=" + PAvg + ",ch=" + TxCh + ",t=" + int(Now - TxMotionMs));
    }

    private void CloseTx(FString Why, int64 Now)
    {
        if (!bTxOpen)
            return;
        bTxOpen = false;
        Line(f"tx {Tx} closed: {Why}");
        Emit("update", "ph=motion,stage=none,tx=" + Tx + ",why=" + (TxMotionMs > 0 ? "lost" : "nomotion"));
        LastTrig = "";
        Recompute("tx closed", Now);
    }

    private int Centered(const TArray<int>& Holes)
    {
        int Count = 0;
        for (int Hole : Holes)
        {
            if (Hole == DsLp::HoleGoal)
                Count++;
        }
        return Count;
    }

    // The stable state changed (ready, select, settle, skill switch): trigger profiles + the next navigation marker.
    // Every trigger "clicks" under the finger (pad Weapon / Bow effect) at the point where the move fires:
    //   click_ok   = Master: this trigger is the next right move - crisp, slightly ringing click;
    //   click_soft = a safe direction (or no information at this skill) - the same click, softer and duller;
    //   wall       = this direction strains the pick - a strong early stop (Trained / Master);
    //   Novice (v32) and Trained (v36): only the safety of L2 / R2 on the selected plate, never the route: safe = click_soft,
    //   dangerous = warn (medium) while the pick takes 2+ strains, wall on its last one (Lives == 1 of a known life).
    private void Recompute(FString Why, int64 Now)
    {
        if (!bReady || bOpenSent || bTxOpen)
            return;
        FString L = "click_soft";
        FString R = "click_soft";
        FString Next = "none";
        bool bWall = Skill() >= 1;
        Next = PlanNext();
        if (bLockKnown && Solver != nullptr && SelPlate >= 0 && Stable.Num() == Plates && Skill() < 2)
        {
            // Novice (v32, user 14.11.2026) / Trained (v36, user 15.11.2026): the lock's physical limits, never the solution
            TArray<int> After;
            int AfterCursor = 0;
            FString Danger = (MaxLife > 0 && Lives == 1) ? "wall" : "warn";
            if (!Solver.Apply(Stable, SelPlate, EDsLp_Key::A, After, AfterCursor))
                L = Danger;
            if (!Solver.Apply(Stable, SelPlate, EDsLp_Key::D, After, AfterCursor))
                R = Danger;
        }
        else if (bLockKnown && Solver != nullptr && SelPlate >= 0 && Stable.Num() == Plates)
        {
            TArray<int> After;
            int AfterCursor = 0;
            if (bWall && !Solver.Apply(Stable, SelPlate, EDsLp_Key::A, After, AfterCursor))
                L = "wall";
            if (bWall && !Solver.Apply(Stable, SelPlate, EDsLp_Key::D, After, AfterCursor))
                R = "wall";
            if (Skill() >= 2 && (Next == "A" || Next == "D" || Next == "W" || Next == "S"))
            {
                // Master (v25): only the plan's move is light; any other pull (wrong way, wrong plate, a strain) is strong.
                L = Next == "A" && L != "wall" ? "path" : "offpath";
                R = Next == "D" && R != "wall" ? "path" : "offpath";
            }
        }
        CurL = L;
        CurR = R;
        CurNext = Next;
        SetTrig(L, R, "", Why);
        GuideDueMs = Now + DsLp::GuideDelayMs;
        if (Why == "settle" && Skill() >= 2 && (Next == "W" || Next == "S"))
        {
            // This action on the current plate is over, not a promise that the plate can never be needed again.
            Emit("update", "ph=leave,cur=" + SelPlate + ",target=" + PlanPlate + ",next=" + Next + ",scope=step");
            GuideDueMs = Now + 260;   // the knock on both triggers comes with it from DsBridge (lp-step-end pad.pulse)
        }
    }

    // ---------------- Trained (v36): progress confirmation + links diagnostics ----------------
    // After a finished, accepted, legal move: the fewest A/D presses to the open lock over the WHOLE state, before and after.
    // v39 (user 15.11.2026): only the move of the ONE adaptive route counts (the plan's next key on its plate, decided at the
    // press, before the tracker replans) and it must really shorten the distance. Leaving the route gets no reward; the tracker
    // replans at once and the next move along the NEW route is rewarded. No event for a strain / reset / break.
    private void EmitProgress(const TArray<int>& Before, bool bHadTx)
    {
        if (Skill() != 1 || !bHadTx || !bTxValid || !bLockKnown || Solver == nullptr || !Solver.IsReady())
            return;
        int D0 = Solver.Distance(Before);
        int D1 = Solver.Distance(Stable);
        Line(f"progress check tx {Tx}: distance {D0} -> {D1}, on route {bTxOnRoute}");
        if (D0 < 0 || D1 < 0 || D1 >= D0 || !bTxOnRoute)
            return;
        Emit("update", "ph=progress,tx=" + Tx + ",d0=" + D0 + ",d1=" + D1 + ",prof=1");
    }

    // After the selector click: how many plates a push of the selected plate moves (itself included; 4 = 4 or more).
    // v37 (user 15.11.2026): goal=1 when this is the plate the adaptive route pushes next - the tracker keeps ONE shortest
    // route and replans at once when the player leaves it (as for Master); DsBridge adds a faint bright ring after the count.
    // Still no direction and no plate number. Trained only; DsBridge pack 47 (can be switched off).
    private void EmitLinks()
    {
        if (Skill() != 1 || !bLockKnown || Solver == nullptr || !Solver.IsReady() || SelPlate < 0)
            return;
        int Moved = Solver.PlatesMoved(SelPlate);
        if (Moved < 1)
            return;
        PlanNext();   // PlanPlate for the current tracker state (Recompute runs after this event)
        int Goal = (PlanPlate >= 0 && PlanPlate == SelPlate) ? 1 : 0;
        Line(f"links: plate {SelPlate} moves {Moved}, route plate {PlanPlate} -> goal {Goal}");
        Emit("update", "ph=links,cur=" + SelPlate + ",k=" + Math::Min(Moved, 4) + ",moved=" + Moved + ",goal=" + Goal + ",prof=1");
    }

    // ---------------- the pick's life: lightbar + player LEDs ----------------
    // Lives = strains the current pick still takes. 5 player LEDs show Lives - 1 (at most 5), the lightbar is the last one:
    // white while 2+ are left, red on the last one, off when the pick broke. Source: the game's LockpickDurability, polled;
    // while the game does not lower it, the mod counts its own predicted strains (conf=derived) and refills on a break.

    private void StartLife()
    {
        float Dur = DsLpGame::Durability();
        LastDurability = Dur;
        MaxLife = Math::RoundToInt(Dur);
        if (MaxLife <= 0)
            MaxLife = 2 + 2 * RealSkill;   // fallback: the skill table (2 / 4 / 6) when the attribute cannot be read
        Lives = MaxLife;
        Line(f"pick life: LockpickDurability {Dur} -> {MaxLife}");
        EmitLives("start");
    }

    private void FollowLife(int64 Now)
    {
        if (MaxLife <= 0)
            return;
        float Dur = DsLpGame::Durability();
        if (Math::Abs(Dur - LastDurability) > 0.01)
        {
            Line(f"pick life: the game changed LockpickDurability {LastDurability} -> {Dur}");
            LastDurability = Dur;
            bLifeFromGame = true;
            LifeRefillAtMs = 0;
            Lives = Math::Clamp(Math::RoundToInt(Dur), 0, 6);
            EmitLives("game");
        }
        if (LifeRefillAtMs > 0 && Now >= LifeRefillAtMs)
            LifeNewPick("refill after the last strain");
    }

    private void LifeStrain(int64 Now)
    {
        if (bLifeFromGame || MaxLife <= 0)
            return;
        Lives = Math::Max(0, Lives - 1);
        if (Lives == 0)
            LifeRefillAtMs = Now + DsLp::LifeRefillMs;
        EmitLives("strain");
    }

    private void LifeNewPick(FString Why)
    {
        LifeRefillAtMs = 0;
        if (bLifeFromGame || MaxLife <= 0 || Lives == MaxLife)
            return;
        Lives = MaxLife;
        EmitLives(Why);
    }

    private void EmitLives(FString Why)
    {
        int Leds = Math::Clamp(Lives - 1, 0, 5);
        FString Bar = Lives >= 2 ? "white" : (Lives == 1 ? "red" : "off");
        FString Ctx = "ph=lives,n=" + Lives + ",max=" + MaxLife + ",leds=" + Leds + ",bar=" + Bar + ",src=" + (bLifeFromGame ? "game" : "derived");
        if (Ctx == LastLives)
            return;
        LastLives = Ctx;
        Line(f"pick life {Lives}/{MaxLife} ({Why}): {Leds} LEDs, lightbar {Bar}");
        Emit("update", Ctx);
        Recompute("life", DsLp::NowMs());   // no-op during motion: the settle recomputes with the new life
    }

    // ---------------- the path: LockPick Helper's tracker ----------------

    // Two agreeing settled readings: the tracker starts (minigame ready + solver ready) or checks its expectation.
    private void FeedTracker(FDsLp_Reading Reading, int64 Now)
    {
        if (!bReady || !bLockKnown || Solver == nullptr || !Solver.IsReady())
            return;
        if (Reading.Visible != Plates || Reading.Holes.Num() != Plates)
            return; // a partial reading (the pieces being taken down), or a lock changed by another mod
        if (Tracker == nullptr)
        {
            Tracker = Cast<UDsLp_Tracker>(NewObject(this, UDsLp_Tracker::StaticClass()));
            Tracker.Init(Solver, Reading.Holes, !bEarlyNav, DsLp::GraceMs);
            FString CursorText = bEarlyNav ? "unknown" : "0";
            Line(f"tracker started: holes {DsLp::HolesText(Reading.Holes)}, cursor {CursorText}, highlight {Reading.Cursor}");
        }
        Tracker.Observe(Reading.Holes, Reading.Cursor, Now);
    }

    // Every press the window heard (keyboard, our L1/R1/L2/R2 relays): the tracker follows the plan or replans.
    private void TrackKey(EDsLp_Key Key)
    {
        if (!bOpen)
            return;
        if (Tracker != nullptr)
            Tracker.Key(Key, DsLp::NowMs());
        else if (Key == EDsLp_Key::W || Key == EDsLp_Key::S)
            bEarlyNav = true;
    }

    // Each frame: when the tracker's plan, position in it or status changed, the feedback is computed again.
    private void FollowPlan(int64 Now)
    {
        if (Tracker != nullptr)
        {
            FDsLp_TrackerSummary Mark = Tracker.Summary();
            int StatusNum = int(Mark.Status);
            if (Mark.Plan != SeenPlan || Mark.Index != SeenIndex || StatusNum != SeenStatus || Mark.Route != SeenRoute)
            {
                bool bNewPlan = Mark.Plan != SeenPlan || StatusNum != SeenStatus;
                bool bNewRoute = !bNewPlan && Mark.Route != SeenRoute;
                SeenRoute = Mark.Route;
                SeenPlan = Mark.Plan;
                SeenIndex = Mark.Index;
                SeenStatus = StatusNum;
                if (bNewPlan)
                {
                    FDsLp_TrackerView View = Tracker.View();
                    FString Cursor = "" + Tracker.Lo;
                    if (Tracker.Lo != Tracker.Hi)
                        Cursor = "" + Tracker.Lo + ".." + Tracker.Hi;
                    FString StatusName = StatusText(Mark.Status);
                    FString KeysLine = DsLp::KeysText(View.Keys);
                    FString HolesLine = DsLp::HolesText(Tracker.Expected);
                    Line(f"plan {Mark.Plan}: {StatusName}, cursor {Cursor}, keys {KeysLine}, holes {HolesLine}");
                    if (Mark.Status == EDsLp_Status::NoSafePath)
                        Emit("update", "ph=solver,status=nopath,plan=" + Mark.Plan);
                }
                else if (bNewRoute)
                {
                    FDsLp_TrackerView View = Tracker.View();
                    FString KeysLine = DsLp::KeysText(View.Keys);
                    Line(f"plan {Mark.Plan} kept, route {Mark.Route} from cursor {Tracker.Lo}: keys {KeysLine}");
                }
                Recompute("plan", Now);
                UpdateSolverLabel();
            }
        }
        else
            UpdateSolverLabel(); // cheap without a tracker: lock note / "preparing"
    }

    private FString StatusText(EDsLp_Status Status)
    {
        if (Status == EDsLp_Status::Ok)
            return "path";
        if (Status == EDsLp_Status::Solved)
            return "solved";
        if (Status == EDsLp_Status::NoSafePath)
            return "NO SAFE PATH";
        return "bad state";
    }

    // The plan's current key for the feedback: W / S / A / D, "nopath", "bad" or "none"; sets PlanPlate.
    private FString PlanNext()
    {
        PlanPlate = -1;
        if (Tracker == nullptr)
            return "none";
        FDsLp_TrackerView View = Tracker.View();
        if (View.Status == EDsLp_Status::NoSafePath)
            return "nopath";
        if (View.Status == EDsLp_Status::BadState)
            return "bad";
        if (View.Status != EDsLp_Status::Ok || View.Index >= View.Keys.Num())
            return "none";
        int Cursor = -1;
        if (Tracker.Lo == Tracker.Hi)
            Cursor = Tracker.Lo;
        for (int i = View.Index; i < View.Keys.Num(); i++)
        {
            EDsLp_Key Each = View.Keys[i];
            if (Each == EDsLp_Key::A || Each == EDsLp_Key::D)
            {
                PlanPlate = Cursor;
                break;
            }
            if (Cursor >= 0 && Each == EDsLp_Key::W)
                Cursor = Math::Min(Cursor + 1, Plates - 1);
            else if (Cursor >= 0 && Each == EDsLp_Key::S)
                Cursor = Math::Max(Cursor - 1, 0);
        }
        return DsLp::KeyLetter(View.Keys[View.Index]);
    }

    // A/D presses left in the plan (-1: no plan).
    private int MovesLeft()
    {
        if (Tracker == nullptr)
            return -1;
        FDsLp_TrackerView View = Tracker.View();
        if (View.Status != EDsLp_Status::Ok)
            return -1;
        int Count = 0;
        for (int i = View.Index; i < View.Keys.Num(); i++)
        {
            if (View.Keys[i] == EDsLp_Key::A || View.Keys[i] == EDsLp_Key::D)
                Count++;
        }
        return Count;
    }

    // TEMPORARY on-screen solver state (below the test label): the path is known / the lock cannot be solved / no solver.
    private void AddSolverLabel(ULockPickUIWidget Window, UCanvasPanel Canvas)
    {
        if (!DsLp::ShowSolverStatus || Canvas == nullptr)
            return;
        if (SolverLabel == nullptr)
        {
            SolverLabel = Cast<UTextBlock>(Window.ConstructWidget(UTextBlock));
            if (SolverLabel == nullptr)
                return;
            FSlateFontInfo Info = SolverLabel.Font;
            UFont Font = Cast<UFont>(DsLpGame::LoadAsset(DsLp::LabelFont));
            if (Font != nullptr)
            {
                Info.FontObject = Font;
                Info.TypefaceFontName = n"Default";
            }
            Info.Size = 22;
            SolverLabel.SetFont(Info);
            SolverLabel.SetShadowOffset(FVector2D(1.5, 1.5));
            SolverLabel.SetJustification(ETextJustify::Center);
        }
        if (SolverLabel.GetParent() == nullptr)
        {
            UCanvasPanelSlot LabelSlot = Canvas.AddChildToCanvas(SolverLabel);
            if (LabelSlot != nullptr)
            {
                LabelSlot.SetAnchors(FAnchors(0.5, 0.0, 0.5, 0.0));
                LabelSlot.SetAlignment(FVector2D(0.5, 0.0));
                LabelSlot.SetPosition(FVector2D(0.0, 100.0));
                LabelSlot.SetAutoSize(true);
                LabelSlot.SetZOrder(100);
            }
        }
        SolverLabel.SetVisibility(ESlateVisibility::HitTestInvisible);
    }

    private void UpdateSolverLabel()
    {
        if (SolverLabel == nullptr)
            return;
        FString Text = "";
        bool bAlarm = false;
        if (LockNote != "")
            Text = LockNote;
        else if (Tracker == nullptr)
        {
            if (bLockKnown)
                Text = "Решатель: готовлю замок...";
        }
        else
        {
            FDsLp_TrackerView View = Tracker.View();
            if (View.Status == EDsLp_Status::NoSafePath)
            {
                Text = "ЗАМОК НЕЛЬЗЯ РЕШИТЬ\nиз этого положения нет пути без поломки отмычки (при текущем навыке героя)";
                bAlarm = true;
            }
            else if (View.Status == EDsLp_Status::BadState)
            {
                Text = "Решатель: положение пластин не подходит к замку - путь неизвестен";
                bAlarm = true;
            }
            else if (View.Status == EDsLp_Status::Solved)
                Text = "Решатель: замок открыт";
            else if (View.Index >= View.Keys.Num())
                Text = "Решатель: путь пройден, ждём игру";
            else
            {
                int Left = MovesLeft();
                int KeysLeft = View.Keys.Num() - View.Index;
                FString CursorNote = "";
                if (Tracker.Lo != Tracker.Hi)
                    CursorNote = "  (курсор уточняется)";
                Text = "Решатель: путь известен - осталось сдвигов " + Left + ", нажатий " + KeysLeft
                     + "\nсейчас: " + DsLp::KeyHint(View.Keys[View.Index]) + CursorNote;
            }
        }
        if (Text == LastSolverText)
            return;
        LastSolverText = Text;
        SolverLabel.SetText(FText::FromString(Text));
        if (bAlarm)
            SolverLabel.SetColorAndOpacity(FSlateColor(FLinearColor(1.0, 0.25, 0.2, 1.0)));
        else
            SolverLabel.SetColorAndOpacity(FSlateColor(FLinearColor(0.75, 0.95, 0.75, 1.0)));
    }

    private void SetTrig(FString L, FString R, FString LoadClass, FString Why)
    {
        FString Ctx = "ph=trig,l=" + L + ",r=" + R + ",next=" + CurNext + ",skill=" + Skill() + ",lives=" + Lives;
        if (LoadClass != "")
            Ctx += ",load=" + LoadClass;
        if (Ctx == LastTrig)
            return;
        LastTrig = Ctx;
        Line(f"triggers ({Why}): L2 {L}, R2 {R}, next {CurNext}, cursor {SelPlate}, lives {Lives}, holes {DsLp::HolesText(Stable)}");
        Emit("update", Ctx);
    }

    // Navigation for the current stable state, once per state (generation) - when the minigame becomes ready, after every
    // plate selection and after every settled move; never during motion (user design 14.11.2026, second version):
    //   wrong plate (next key W / S) -> kind=sel: ONE side click toward the button to press: right = R1 (up, W), left = L1 (down, S);
    //   right plate (next key A / D) -> kind=plate: ONE centre click (both halves); Master: then ONE side click = push direction (L2 / R2);
    //   Trained: sel + the centre click, no push direction; Novice -> kind=off; nopath / bad -> no invented direction.
    private void EmitGuide()
    {
        if (!bReady || bOpenSent)
            return;
        int Prof = Skill();
        FString Kind = "none";
        FString Side = "-";
        if (Prof < 2)
            Kind = "off";   // v36: Novice and Trained get no navigation (target plate, direction); only Master
        else if (CurNext == "W" || CurNext == "S")
        {
            Kind = "sel";
            Side = CurNext == "W" ? DsLp::SideUp : DsLp::SideDown;
        }
        else if (CurNext == "A" || CurNext == "D")
        {
            Kind = "plate";
            if (Prof >= 2)
                Side = CurNext == "A" ? "L" : "R";
        }
        else if (CurNext == "nopath" || CurNext == "bad")
            Kind = CurNext;
        GuideGen++;
        int TrackerCursor = -1;
        if (Tracker != nullptr && Tracker.Lo == Tracker.Hi)
            TrackerCursor = Tracker.Lo;
        Line(f"guide {GuideGen}: kind {Kind}, side {Side}, next {CurNext}, cursor {SelPlate}, tracker cursor {TrackerCursor}, target {PlanPlate}");
        Emit("update", "ph=guide,gen=" + GuideGen + ",kind=" + Kind + ",side=" + Side + ",next=" + CurNext + ",cur=" + SelPlate + ",target=" + PlanPlate + ",prof=" + Prof
                       + ",plan=" + SeenPlan + ",left=" + MovesLeft());
    }

    // ---------------- the window's press events (every press: keyboard, our L2/R2 and D-pad relays) ----------------

    private void Listen(ULockPickUIWidget Window)
    {
        if (bListening)
            return;
        bListening = true;
        Window.m_OnUpPressed.AddUFunction(this, n"OnUp");
        Window.m_OnDownPressed.AddUFunction(this, n"OnDown");
        Window.m_OnLeftPressed.AddUFunction(this, n"OnLeft");
        Window.m_OnRightPressed.AddUFunction(this, n"OnRight");
        Window.m_OnResetPressed.AddUFunction(this, n"OnReset");
    }

    UFUNCTION()
    private void OnUp()
    {
        Line("press Up");
        bool bEdge = AtEdge(EDsLp_Key::W);
        TrackKey(EDsLp_Key::W);
        NavigationContact("W", bEdge);
    }

    UFUNCTION()
    private void OnDown()
    {
        Line("press Down");
        bool bEdge = AtEdge(EDsLp_Key::S);
        TrackKey(EDsLp_Key::S);
        NavigationContact("S", bEdge);
    }

    // v38: the cursor is already on the last plate in this direction (R1 / W on the top plate, L1 / S on the bottom one): the press
    // hits the mechanism's end stop. The tracker's cursor follows every press at once (the reading lags ~50 ms), so a quick
    // "down, up" at the top is not a false stop; an unknown tracker cursor falls back to the read one.
    private bool AtEdge(EDsLp_Key Key)
    {
        if (!bReady || Plates < 1)
            return false;
        int Cur = SelPlate;
        if (Tracker != nullptr && Tracker.Lo == Tracker.Hi)
            Cur = Tracker.Lo;
        if (Cur < 0)
            return false;
        if (Key == EDsLp_Key::W)
            return Cur >= Plates - 1;
        return Cur <= 0;
    }

    private void NavigationContact(FString Key, bool bEdge)
    {
        if (!bOpen || !bReady)
            return;
        GuideDueMs = DsLp::NowMs() + DsLp::GuideDelayMs;
        NavPresses++;
        if (bEdge)
            Line(f"end stop: {Key} on plate {SelPlate} of {Plates}");
        Emit("update", "ph=navpress,k=" + Key + ",cur=" + SelPlate + ",nav=" + NavPresses + ",requested=1,edge=" + (bEdge ? 1 : 0));
    }

    UFUNCTION()
    private void OnLeft()
    {
        bool bOnRoute = PlanNext() == "A";
        HeardMove(-1);
        bTxOnRoute = bOnRoute;
        TrackKey(EDsLp_Key::A);
    }

    UFUNCTION()
    private void OnRight()
    {
        bool bOnRoute = PlanNext() == "D";
        HeardMove(1);
        bTxOnRoute = bOnRoute;
        TrackKey(EDsLp_Key::D);
    }

    UFUNCTION()
    private void OnReset()
    {
        if (!bOpen)
            return;
        LastResetMs = DsLp::NowMs();
        Line("press Reset");
        TrackKey(EDsLp_Key::R);
    }

    // A move press (L2/R2 of the catcher or the keyboard's own A/D): a new move id (tx) and the solver's projection of it
    // (the same transition function the solver uses: Solver.Apply). Nothing is claimed yet: the press event says
    // conf=predicted; physics follows only observed motion (TrackMotion) and the settled layout (HandleReading).
    private void HeardMove(int Dir)
    {
        if (!bOpen)
            return;
        int64 Now = DsLp::NowMs();
        FString Side = Dir < 0 ? "L" : "R";
        if (!bReady)
        {
            Line(f"press {Side} before the minigame is ready (the game ignores it)");
            return;
        }
        if (bTxOpen)
            Line(f"press {Side} while tx {Tx} is open (motion {TxMotionMs > 0})");
        Tx++;
        bTxOpen = true;
        TxPressMs = Now;
        TxMotionMs = 0;
        TxDir = Dir;
        bTxValid = false;
        TxCh = 0;
        TxSame = 0;
        TxOpp = 0;
        GuideDueMs = 0;
        FDsLp_Reading Fresh = DsLpGame::ReadPlates(Plates);
        TxStartDx = Fresh.Offsets;
        TxBefore = Stable;
        TxAfter = Stable;
        TxCrossed.SetNum(Plates);
        for (int i = 0; i < Plates; i++)
            TxCrossed[i] = false;
        FString Valid = "unknown";
        int Sel = SelPlate;
        if (bLockKnown && Solver != nullptr && Fresh.bSettled && Fresh.Holes.Num() == Plates)
        {
            if (Fresh.Cursor >= 0)
                Sel = Fresh.Cursor;
            TxBefore = Fresh.Holes;
            TxAfter = Fresh.Holes;
            if (Sel >= 0)
            {
                TArray<int> After;
                int AfterCursor = 0;
                EDsLp_Key Key = Dir < 0 ? EDsLp_Key::A : EDsLp_Key::D;
                if (Solver.Apply(Fresh.Holes, Sel, Key, After, AfterCursor))
                {
                    bTxValid = true;
                    TxAfter = After;
                    Valid = "1";
                }
                else
                    Valid = "0";
            }
        }
        int CenterIn = 0;
        int CenterOut = 0;
        int Mask = 0;
        if (bTxValid)
        {
            for (int i = 0; i < Plates; i++)
            {
                int Delta = TxAfter[i] - TxBefore[i];
                if (Delta == 0)
                    continue;
                TxCh++;
                Mask = Mask | (1 << i);
                if (Delta == Dir)
                    TxSame++;
                else
                    TxOpp++;
                if (TxAfter[i] == DsLp::HoleGoal)
                    CenterIn++;
                if (TxBefore[i] == DsLp::HoleGoal)
                    CenterOut++;
            }
        }
        Line(f"press {Side} tx {Tx} on plate {Sel}: valid {Valid}, holes {DsLp::HolesText(TxBefore)} -> {DsLp::HolesText(TxAfter)} (plates {TxCh}, same {TxSame}, opposite {TxOpp})");
        Emit("update", "ph=press,tx=" + Tx + ",dir=" + Side + ",cur=" + Sel + ",valid=" + Valid + ",ch=" + TxCh + ",same=" + TxSame + ",opp=" + TxOpp
                       + ",cin=" + CenterIn + ",cout=" + CenterOut + ",mask=" + Mask + ",conf=predicted,via=" + (bPadFire ? "pad" : "key"));
        if (Valid == "0")
        {
            Strains++;
            LastStrainMs = Now;
            LifeStrain(Now);
            Line(f"press {Side} on plate {Sel}: STRAIN predicted (holes {DsLp::HolesText(TxBefore)})");
            Emit("update", "ph=strain,dir=" + Side + ",cur=" + Sel + ",tx=" + Tx + ",conf=derived");
        }
    }

    // ---------------- L2 / R2 ----------------
    // The trigger "click" (Weapon / Bow effect of the pad, set by DsBridge from the trig profiles) breaks at a fixed point of
    // the travel. The move is fired when the analog value passes FirePoint, set just after that break (DsLp_Lockpick.ini,
    // written by `lp-bench` calibration), so the order is: press -> click under the finger -> game move -> plates -> HD.
    // The digital trigger key fires only while no analog value has ever been seen (fallback; its threshold is the
    // engine's, logged with the analog value at that moment).

    private void ForgetTriggers()
    {
        // held triggers are let go (focus lost, device change, window closing): DsBridge must not keep a parked trigger off
        if (bLHeld)
            LetGo(false);
        if (bRHeld)
            LetGo(true);
        bLHeld = false;
        bRHeld = false;
        bLDigital = false;
        bRDigital = false;
        LAnalog = 0.0;
        RAnalog = 0.0;
    }

    // DsLp_Lockpick.ini in the mod's folder: FirePoint=0.58, ReleasePoint=0.25 (lines key=value; missing = defaults).
    private void ReadSettings()
    {
        FString Raw;
        FString IniPath = FPaths::ProjectDir() + "Mods/G1R_DualSenseBridge/DsLp_Lockpick.ini";
        if (!FFileHelper::LoadFileToString(Raw, IniPath))
        {
            Line(f"settings: no DsLp_Lockpick.ini, defaults: fire point {FirePoint}, release point {ReleasePoint}");
            return;
        }
        TArray<FString> Rows = String::ParseIntoArray(Raw, "\n", true);
        for (FString Row : Rows)
        {
            TArray<FString> Kv = String::ParseIntoArray(Row, "=", true);
            if (Kv.Num() < 2)
                continue;
            float Value = float(String::Conv_StringToDouble(Kv[1]));
            if (Kv[0] == "FirePoint")
                FirePoint = Math::Clamp(Value, 0.15, 0.95);
            else if (Kv[0] == "ReleasePoint")
                ReleasePoint = Math::Clamp(Value, 0.05, 0.6);
        }
        if (ReleasePoint > FirePoint - 0.1)
            ReleasePoint = Math::Max(0.05, FirePoint - 0.1);
        Line(f"settings (DsLp_Lockpick.ini): fire point {FirePoint}, release point {ReleasePoint}");
    }

    // Digital trigger key (Gamepad_LeftTrigger / Gamepad_RightTrigger) down or up.
    void TriggerDigital(bool bRight, bool bDown)
    {
        if (!bRight)
        {
            if (bDown)
            {
                bLDigital = true;
                if (bAnalogSeen)
                    Line(f"L2 digital key at analog {LAnalog}");
                if (!bLHeld && !bAnalogSeen)
                {
                    bLHeld = true;
                    FireMove(false, "digital");
                }
            }
            else
            {
                bLDigital = false;
                if (!bAnalogSeen || LAnalog <= ReleasePoint)
                    LetGo(false);
            }
            return;
        }
        if (bDown)
        {
            bRDigital = true;
            if (bAnalogSeen)
                Line(f"R2 digital key at analog {RAnalog}");
            if (!bRHeld && !bAnalogSeen)
            {
                bRHeld = true;
                FireMove(true, "digital");
            }
        }
        else
        {
            bRDigital = false;
            if (!bAnalogSeen || RAnalog <= ReleasePoint)
                LetGo(true);
        }
    }

    // Analog trigger axis value 0..1: fire at FirePoint (just past the click), re-arm below ReleasePoint (hysteresis).
    void TriggerAnalog(bool bRight, float Value)
    {
        if (!bAnalogSeen)
        {
            bAnalogSeen = true;
            Line(f"analog trigger values arrive: moves now fire at {FirePoint}");
        }
        if (!bRight)
        {
            LAnalog = Value;
            if (!bLHeld && Value >= FirePoint)
            {
                bLHeld = true;
                FireMove(false, "analog");
            }
            else if (bLHeld && Value <= ReleasePoint)
                LetGo(false);
            return;
        }
        RAnalog = Value;
        if (!bRHeld && Value >= FirePoint)
        {
            bRHeld = true;
            FireMove(true, "analog");
        }
        else if (bRHeld && Value <= ReleasePoint)
            LetGo(true);
    }

    // The finger let go of a trigger (below ReleasePoint): re-armed here, and DsBridge gets ph=trigup so the trigger's click
    // profile comes back only now (after the click's vibration tail a held trigger stays without resistance - "Сухой спуск").
    private void LetGo(bool bRight)
    {
        bool bWas = bRight ? bRHeld : bLHeld;
        if (bRight)
            bRHeld = false;
        else
            bLHeld = false;
        if (bWas && bOpen)
            Emit("update", "ph=trigup,side=" + (bRight ? "R" : "L"));
    }

    // One real pull of the player -> the game's own left / right event.
    private void FireMove(bool bRight, FString Via)
    {
        ULockPickUIWidget Window = Cast<ULockPickUIWidget>(GetWidget());
        if (!bOpen || Window == nullptr)
            return;
        Line(bRight ? f"R2 pulled ({Via}, analog {RAnalog}, click profile {CurR}) -> Right" : f"L2 pulled ({Via}, analog {LAnalog}, click profile {CurL}) -> Left");
        bPadFire = true;   // the window's handler runs inside Broadcast: HeardMove marks the press via=pad
        if (bRight)
            Window.m_OnRightPressed.Broadcast();
        else
            Window.m_OnLeftPressed.Broadcast();
        bPadFire = false;
    }

    // D-pad Up / Down from the catcher -> the game's own Up / Down (the catcher holds the focus the D-pad navigated).
    void RelayVertical(bool bUp)
    {
        ULockPickUIWidget Window = Cast<ULockPickUIWidget>(GetWidget());
        if (!bOpen || Window == nullptr)
            return;
        if (bUp)
            Window.m_OnUpPressed.Broadcast();
        else
            Window.m_OnDownPressed.Broadcast();
    }

    // A key the catcher saw (diagnostics).
    void KeySeen(FKey Key, bool bRepeat, FString What)
    {
        if (!bRepeat)
            Line(f"key {Key.KeyName}: {What}");
    }

    // ---------------- key catcher, focus ----------------

    private void AddCatcher(ULockPickUIWidget Window, UCanvasPanel Canvas)
    {
        if (Canvas == nullptr)
            return;
        if (Catcher == nullptr)
        {
            Catcher = Cast<UDsLp_KeyCatcher>(Window.ConstructWidget(UDsLp_KeyCatcher, n"DsLp_KeyCatcher"));
            if (Catcher == nullptr)
            {
                Line("the key catcher was not made: L2/R2 do not work");
                return;
            }
            Catcher.Owner = this;
            Catcher.SetbIsFocusable(true);
            Catcher.SetVisibility(ESlateVisibility::Visible);
        }
        if (Catcher.GetParent() == nullptr)
        {
            UCanvasPanelSlot Slot = Canvas.AddChildToCanvas(Catcher);
            if (Slot != nullptr)
                Slot.SetSize(FVector2D(1.0, 1.0));
        }
    }

    private bool TakeFocus()
    {
        Catcher.ForgetHeld();
        ForgetTriggers();
        APlayerController Player = Gameplay::GetPlayerController(0);
        if (Player != nullptr)
            Catcher.SetUserFocus(Player);
        Catcher.SetKeyboardFocus();
        return Catcher.HasKeyboardFocus();
    }

    // Alt+Tab (and the game) take the focus away: taken back every FocusRetryMs; held triggers are forgotten then.
    private void FollowFocus(int64 Now)
    {
        if (Catcher == nullptr || Now - FocusCheckMs < DsLp::FocusRetryMs)
            return;
        FocusCheckMs = Now;
        if (Catcher.HasKeyboardFocus())
        {
            if (bFocusLost)
                Line("key catcher: focus is back");
            bFocusLost = false;
            return;
        }
        if (!bFocusLost)
        {
            bFocusLost = true;
            Line("key catcher: focus lost; taking it back");
        }
        if (TakeFocus())
        {
            Line("key catcher: focus is back");
            bFocusLost = false;
        }
    }

    // ---------------- legend: the minigame's move buttons -> PS L2 / R2 / L1 / R1 ----------------
    // W_LockPickUI draws its legend with W_GenericButton_C widgets Button_MoveLeft / Right / Up / Down, each with a
    // W_InputHint_C whose Image_Hint shows the key (session 76 tree dump: T_Icon_PS_DpadLeft / Right / Up / Down).
    // The image is found by the button it is in, so other D-pad icons of the window (the item slot's hotkey) are untouched.

    private UTexture2D IconFor(FString Button)
    {
        if (Button == "Button_MoveLeft")
            return L2Tex;
        if (Button == "Button_MoveRight")
            return R2Tex;
        if (Button == "Button_MoveUp")
            return R1Tex;
        if (Button == "Button_MoveDown")
            return L1Tex;
        return nullptr;
    }

    private void UpdatePrompts(ULockPickUIWidget Window)
    {
        int InputDev = DsLpGame::InputType();
        bool bPad = InputDev == 1;
        if (L2Tex == nullptr)
            L2Tex = Cast<UTexture2D>(DsLpGame::LoadAsset(DsLp::IconDir + "T_Icon_PS_L2.T_Icon_PS_L2"));
        if (R2Tex == nullptr)
            R2Tex = Cast<UTexture2D>(DsLpGame::LoadAsset(DsLp::IconDir + "T_Icon_PS_R2.T_Icon_PS_R2"));
        if (L1Tex == nullptr)
            L1Tex = Cast<UTexture2D>(DsLpGame::LoadAsset(DsLp::IconDir + "T_Icon_PS_L1.T_Icon_PS_L1"));
        if (R1Tex == nullptr)
            R1Tex = Cast<UTexture2D>(DsLpGame::LoadAsset(DsLp::IconDir + "T_Icon_PS_R1.T_Icon_PS_R1"));
        TArray<UImage> Images;
        TArray<FString> Buttons;
        FString Dump = "";
        bool bDump = !bTreeLogged;
        Collect(Window.GetRootWidget(), Images, Buttons, "", 0, bDump, Dump);
        if (bDump)
        {
            bTreeLogged = true;
            Line(f"widget tree of the window (icons loaded: L2 {L2Tex != nullptr}, R2 {R2Tex != nullptr}, L1 {L1Tex != nullptr}, R1 {R1Tex != nullptr}):\n{Dump}");
        }

        int Swapped = 0;
        int Restored = 0;
        for (int i = 0; i < Images.Num(); i++)
        {
            UImage Img = Images[i];
            if (Img == nullptr || Img.GetName() != n"Image_Hint")
                continue;
            UTexture2D Want = IconFor(Buttons[i]);
            if (Want == nullptr)
                continue;
            UTexture2D Tex = Widget::GetBrushResourceAsTexture2D(Img.Brush);
            if (bPad)
            {
                if (Tex != Want)
                {
                    if (Tex != nullptr)
                        Remember(Img, Tex);
                    Img.SetBrushFromTexture(Want, false);
                    Swapped++;
                }
            }
            else if (Tex == Want)
            {
                int Idx = SwappedImages.FindIndex(Img);
                if (Idx >= 0 && SwappedOriginals[Idx] != nullptr)
                {
                    Img.SetBrushFromTexture(SwappedOriginals[Idx], false);
                    Restored++;
                }
            }
        }
        if (InputDev != LastInputType || Swapped > 0 || Restored > 0)
        {
            if (InputDev != LastInputType)
                Line(f"input device {InputDev} (1 = gamepad); images in the window {Images.Num()}");
            if (Swapped > 0)
                Line(f"legend: {Swapped} icon(s) set to L2/R2/L1/R1");
            if (Restored > 0)
                Line(f"legend: {Restored} icon(s) restored for keyboard");
            if (InputDev != LastInputType && LastInputType >= 0)
            {
                ForgetTriggers();
                Emit("update", "ph=device,input=" + InputDev);
            }
            LastInputType = InputDev;
        }
    }

    private void Remember(UImage Img, UTexture2D Original)
    {
        if (SwappedImages.FindIndex(Img) >= 0)
            return;
        SwappedImages.Add(Img);
        SwappedOriginals.Add(Original);
    }

    // Every UImage under a widget (panels, user widgets) with the name of the nearest "Button_*" widget around it, and a
    // dump of the tree for the log.
    private void Collect(UWidget W, TArray<UImage>& OutImages, TArray<FString>& OutButtons, FString Button, int Depth, bool bDump, FString& Dump)
    {
        if (W == nullptr || Depth > 30)
            return;
        if (W == Catcher || W == TestLabel || W == SolverLabel)
            return;
        FString WName = W.GetName().ToString();
        FString Here = Button;
        if (WName.StartsWith("Button_"))
            Here = WName;
        UImage Img = Cast<UImage>(W);
        if (Img != nullptr)
        {
            OutImages.Add(Img);
            OutButtons.Add(Here);
        }
        if (bDump)
        {
            FString Pad = "";
            for (int i = 0; i < Depth; i++)
                Pad += "  ";
            FString Extra = "";
            if (Img != nullptr)
            {
                UTexture2D Tex = Widget::GetBrushResourceAsTexture2D(Img.Brush);
                Extra = Tex != nullptr ? " tex=" + Tex.GetName().ToString() : " tex=-";
            }
            Dump += Pad + W.Class.GetName().ToString() + " " + WName + " vis=" + int(W.GetVisibility()) + Extra + "\n";
        }
        UUserWidget User = Cast<UUserWidget>(W);
        if (User != nullptr)
            Collect(User.GetRootWidget(), OutImages, OutButtons, Here, Depth + 1, bDump, Dump);
        UPanelWidget Panel = Cast<UPanelWidget>(W);
        if (Panel != nullptr)
        {
            TArray<UWidget> Kids = Panel.GetAllChildren();
            for (UWidget Kid : Kids)
                Collect(Kid, OutImages, OutButtons, Here, Depth + 1, bDump, Dump);
        }
    }

    // ---------------- output ----------------

    // v34: the pick breaks -> one lp_break:start at the entry into anim state 4 (= the start of A_Lockpicking_BrokenPick, matched
    // to the clip's bone poses in the v33 trace). 1 = idle, 2 = a move, 4 = broken; 4 stays after the clips, so the observer
    // re-arms only after it sees another state. The DsBridge scene carries its own 336 / 1502 ms notify delays.
    private void FollowBreakAnim(int64 Now)
    {
        if (BreakMesh == nullptr)
        {
            if (Now - BreakTryMs < 500)
                return;
            BreakTryMs = Now;
            TArray<AGothicLockPickActor> Actors;
            GetAllActorsOfClass(AGothicLockPickActor::StaticClass(), Actors);
            for (AGothicLockPickActor Actor : Actors)
            {
                if (Actor == nullptr || Actor.bHidden)
                    continue;
                TArray<UActorComponent> Components = Actor.GetComponentsByClass(USkeletalMeshComponent::StaticClass());
                for (UActorComponent Component : Components)
                {
                    USkeletalMeshComponent Mesh = Cast<USkeletalMeshComponent>(Component);
                    if (Mesh != nullptr && Cast<ULockPickAnimInstance>(Mesh.GetAnimInstance()) != nullptr)
                    {
                        BreakMesh = Mesh;
                        break;
                    }
                }
                if (BreakMesh != nullptr)
                    break;
            }
            if (BreakMesh == nullptr)
                return;
            Line(f"break observer: lock pick mesh {BreakMesh.GetName()} after {Now - OpenedMs} ms");
        }
        ULockPickAnimInstance Anim = Cast<ULockPickAnimInstance>(BreakMesh.GetAnimInstance());
        if (Anim == nullptr)
            return;
        int State = int(Anim.m_LockPickAnimState);
        if (State == BreakState)
            return;
        int Prev = BreakState;
        BreakState = State;
        if (State != 4)
        {
            bBreakArmed = true;
            return;
        }
        if (!bBreakArmed)
        {
            Line(f"break observer: anim state 4 already at open ({Now - OpenedMs} ms), no event");
            return;
        }
        bBreakArmed = false;
        BreakAnimMs = Now;
        Breaks++;
        Line(f"PICK BREAKS (anim state {Prev} -> 4) at {Now - OpenedMs} ms, {Now - LastStrainMs} ms after the last strain, lives {Lives}, break {Breaks}");
        if (Bus != nullptr)
            Bus.Emit("lp_break", "start", "lockpick", "state=4,prev=" + Prev + ",tx=" + Tx + ",lives=" + Lives + ",n=" + Breaks + ",ver=" + DsLp::ModVersion);
        BreakP1 = BreakMesh.GetSocketLocation(n"pick_1") - BreakMesh.GetSocketLocation(n"root");
        bOutPending = true;
    }

    // v35: the second part - A_Lockpicking_BrokenPick_Out pulls the broken tip (bone pick_1) out of the lock. It has no notify
    // and its start varies (one fit: ~2316 ms after state 4), so the mod watches pick_1 itself: one lp_break:update ph=out.
    private void FollowBreakOut(int64 Now)
    {
        if (!bOutPending || BreakMesh == nullptr)
            return;
        int64 Since = Now - BreakAnimMs;
        if (Since < DsLp::OutArmMs)
            return;
        FVector P1 = BreakMesh.GetSocketLocation(n"pick_1") - BreakMesh.GetSocketLocation(n"root");
        float Moved = (P1 - BreakP1).Size();
        FString Src = "";
        if (Moved > DsLp::OutMoveWorld)
            Src = "bone";
        else if (Since >= DsLp::OutTimerMs)
            Src = "timer";
        if (Src == "")
            return;
        bOutPending = false;
        int MovedX100 = Math::RoundToInt(Moved * 100.0);
        Line(f"BROKEN TIP OUT ({Src}) at {Since} ms after the break, pick_1 moved {Moved}");
        if (Bus != nullptr)
            Bus.Emit("lp_break", "update", "lockpick", "ph=out,src=" + Src + ",since=" + int(Since) + ",d100=" + MovedX100 + ",n=" + Breaks);
    }

    private void FollowFailureAnimationTrace(int64 Now)
    {
        if (FailureTraceMesh == nullptr && Now - FailureTraceTryMs >= 500)
        {
            FailureTraceTryMs = Now;
            TArray<AGothicLockPickActor> Actors;
            GetAllActorsOfClass(AGothicLockPickActor::StaticClass(), Actors);
            for (AGothicLockPickActor Actor : Actors)
            {
                if (Actor == nullptr || Actor.bHidden)
                    continue;
                TArray<UActorComponent> Components = Actor.GetComponentsByClass(USkeletalMeshComponent::StaticClass());
                for (UActorComponent Component : Components)
                {
                    USkeletalMeshComponent Mesh = Cast<USkeletalMeshComponent>(Component);
                    if (Mesh != nullptr && Cast<ULockPickAnimInstance>(Mesh.GetAnimInstance()) != nullptr)
                    {
                        FailureTraceMesh = Mesh;
                        break;
                    }
                }
                if (FailureTraceMesh != nullptr)
                    break;
            }
        }
        if (FailureTraceMesh == nullptr)
            return;
        ULockPickAnimInstance Anim = Cast<ULockPickAnimInstance>(FailureTraceMesh.GetAnimInstance());
        if (Anim == nullptr)
            return;
        int State = int(Anim.m_LockPickAnimState);
        bool bChanged = State != FailureTraceState;
        if (bChanged)
        {
            Line(f"ANIMSTATE abs={Now} open={Now - OpenedMs} state={State} prev={FailureTraceState} mesh={FailureTraceMesh.GetName()}");
            Bus.Emit("lp_anim", "update", "anim_trace", "state=" + State + ",prev=" + FailureTraceState);
            FailureTraceState = State;
        }
        // Baseline plus a five-second window after each strain; do not flood ordinary play.
        if (!bChanged && Now - OpenedMs > 1000 && Now - LastStrainMs > 5000)
            return;
        if (Now - FailureTraceSampleMs < 16 && !bChanged)
            return;
        FailureTraceSampleMs = Now;
        FVector P1 = FailureTraceMesh.GetSocketLocation(n"pick_1");
        FVector P2 = FailureTraceMesh.GetSocketLocation(n"pick_2");
        FVector Root = FailureTraceMesh.GetSocketLocation(n"root");
        FRotator RootRot = FailureTraceMesh.GetSocketRotation(n"root");
        Line(f"ANIMPOSE abs={Now} open={Now - OpenedMs} state={State} p1x={P1.X} p1y={P1.Y} p1z={P1.Z} p2x={P2.X} p2y={P2.Y} p2z={P2.Z} rx={Root.X} ry={Root.Y} rz={Root.Z} pitch={RootRot.Pitch} yaw={RootRot.Yaw} roll={RootRot.Roll}");
    }

    private void Emit(FString Phase, FString Ctx)
    {
        if (Bus != nullptr)
            Bus.Emit("lp", Phase, "lockpick", Ctx);
    }

    private void Line(FString Text)
    {
        if (Bus != nullptr && Bus.m_Log != nullptr)
            Bus.m_Log.Line(Text);
    }
}

// The key catcher: an invisible focusable 1x1 widget in the window's canvas (inside the minigame keys reach only the focused
// widget - LockPick Helper's finding). Gamepad triggers and D-pad are handled here; every other key bubbles up unchanged.
class UDsLp_KeyCatcher : UUserWidget
{
    UPROPERTY()
    UDsLp_LockpickExtension Owner;

    private TArray<FKey> Held;

    void ForgetHeld()
    {
        Held.Reset();
    }

    // 0 not ours, 1 L2, 2 R2, 3 L1 (plate down), 4 R1 (plate up), 5 D-pad (swallowed: selection is on L1/R1, moves on L2/R2),
    // 6 TEMPORARY skill profile switch (Square / F7)
    int KindOf(FKey Key)
    {
        if (Key.KeyName == n"Gamepad_LeftTrigger")
            return 1;
        if (Key.KeyName == n"Gamepad_RightTrigger")
            return 2;
        if (Key.KeyName == n"Gamepad_LeftShoulder")
            return 3;
        if (Key.KeyName == n"Gamepad_RightShoulder")
            return 4;
        if (Key.KeyName == n"Gamepad_DPad_Up" || Key.KeyName == n"Gamepad_DPad_Down" || Key.KeyName == n"Gamepad_DPad_Left" || Key.KeyName == n"Gamepad_DPad_Right")
            return 5;
        if (DsLp::TestSkillSwitch && (Key.KeyName == n"Gamepad_FaceButton_Left" || Key.KeyName == n"F7"))
            return 6;
        return 0;
    }

    UFUNCTION(BlueprintOverride)
    FEventReply OnKeyDown(FGeometry MyGeometry, FKeyEvent InKeyEvent)
    {
        FKey Key = Input::GetKey(InKeyEvent);
        bool bRepeat = Held.Contains(Key);
        if (!bRepeat)
            Held.Add(Key);
        int Kind = KindOf(Key);
        if (Owner == nullptr)
            return FEventReply::Unhandled();
        if (Kind == 0)
        {
            Owner.KeySeen(Key, bRepeat, "to the minigame");
            return FEventReply::Unhandled();
        }
        if (Kind == 1 || Kind == 2)
        {
            if (!bRepeat)
                Owner.TriggerDigital(Kind == 2, true);
            return FEventReply::Handled();
        }
        if (Kind == 3 || Kind == 4)
        {
            Owner.KeySeen(Key, bRepeat, "selection");
            if (!bRepeat)
                Owner.RelayVertical(Kind == 4);
            return FEventReply::Handled();
        }
        if (Kind == 6)
        {
            if (!bRepeat)
                Owner.CycleSkill();
            return FEventReply::Handled();
        }
        Owner.KeySeen(Key, bRepeat, "ignored (L1/R1 select, L2/R2 move)");
        return FEventReply::Handled();
    }

    UFUNCTION(BlueprintOverride)
    FEventReply OnKeyUp(FGeometry MyGeometry, FKeyEvent InKeyEvent)
    {
        FKey Key = Input::GetKey(InKeyEvent);
        Held.Remove(Key);
        int Kind = KindOf(Key);
        if (Kind == 0)
            return FEventReply::Unhandled();
        if ((Kind == 1 || Kind == 2) && Owner != nullptr)
            Owner.TriggerDigital(Kind == 2, false);
        return FEventReply::Handled();
    }

    UFUNCTION(BlueprintOverride)
    FEventReply OnAnalogValueChanged(FGeometry MyGeometry, FAnalogInputEvent InAnalogInputEvent)
    {
        FKey Key = Input::GetKey(Widget::GetKeyEventFromAnalogInputEvent(InAnalogInputEvent));
        if (Owner == nullptr)
            return FEventReply::Unhandled();
        if (Key.KeyName == n"Gamepad_LeftTriggerAxis")
        {
            Owner.TriggerAnalog(false, Input::GetAnalogValue(InAnalogInputEvent));
            return FEventReply::Handled();
        }
        if (Key.KeyName == n"Gamepad_RightTriggerAxis")
        {
            Owner.TriggerAnalog(true, Input::GetAnalogValue(InAnalogInputEvent));
            return FEventReply::Handled();
        }
        return FEventReply::Unhandled();
    }
}
