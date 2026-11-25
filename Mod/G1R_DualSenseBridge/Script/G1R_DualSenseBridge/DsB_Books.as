// Book puzzle probe (mod v51, user 23.11.2026: "a click when a book is pulled in the secret-passage puzzle (Mountain Fortress,
// the room past the bridge with the stone golem), a different signal for the right and the wrong order").
// Read-only. The hero pulls a book with Man_Secret_Door_Book_Montage (the click = its Interact notify at 2.52 s); the
// bookshelves are triggers (UGenericTriggerBookShelve*, UTriggerExtraData: steps, m_BackToStart) and the order is checked by
// the game's puzzle subsystem (UPuzzlesSubsystem: per puzzle m_PuzzleDone = steps done so far, m_FinishedPuzzles).
// The level logic itself is not in the scripts, so the answer is read from what changes right after the click:
//   book:start  obj=<bookshelf actor>,def=<definition class>,near=<bookshelves within 15 m>
//   book:update res=ok|reset|solved|unk,look=click|late,done0=<before>,done=<after>,fin0,fin,moving=<other shelves animating>,obj
//     solved: a puzzle finished; ok: steps done grew; reset: steps done fell or the other books moved back; unk: no change seen
//   book:complete obj=                       (the hero's book montage ended)
// [Г] until the first game test: which of these the books really change. The journal keeps every number for the analysis.
namespace DsBBooks
{
    const float ProbeSec = 0.05;
    const float JudgeSec = 2.85;      // Interact notify 2.52 s + time for the trigger and the other shelves to react
    const float LateSec = 4.2;        // second look: books that roll back after the answer
    const float NearCm = 1500.0;

    bool IsShelf(AInteractiveObjectActor Object)
    {
        if (Object == nullptr || Object.GetInteractiveObjectDefinition() == nullptr)
            return false;
        return Object.GetInteractiveObjectDefinition().GetClass().GetName().ToString().Contains("BookShelve");
    }

    UObject PuzzleSubsystem()
    {
        // v51 game 23.11: the compiler accepts UPuzzlesSubsystem only as a world subsystem (the game-instance lookup did not compile)
        UObject S = Subsystem::GetWorldSubsystem(UPuzzlesSubsystem::StaticClass());
        if (Cast<UPuzzlesSubsystem>(S) != nullptr)
            return S;
        return nullptr;
    }

    int StepsDone()
    {
        UPuzzlesSubsystem P = Cast<UPuzzlesSubsystem>(PuzzleSubsystem());
        if (P == nullptr)
            return -1;
        int N = 0;
        for (int I = 0; I < P.m_PuzzleStepArray.Num(); I++)
            N += P.m_PuzzleStepArray[I].m_PuzzleDone.Num();
        return N;
    }

    int Finished()
    {
        UPuzzlesSubsystem P = Cast<UPuzzlesSubsystem>(PuzzleSubsystem());
        if (P == nullptr)
            return -1;
        return P.m_FinishedPuzzles.Num();
    }

    AInteractiveObjectActor UsedObject(AGothicCharacter Hero)
    {
        UAbilitySystemComponent Asc = Hero.GetAbilitySystemComponent();
        if (Asc == nullptr)
            return nullptr;
        TArray<FGameplayAbilitySpecHandle> Handles;
        Asc.GetAllAbilities(Handles);
        for (FGameplayAbilitySpecHandle Handle : Handles)
        {
            FGameplayAbilitySpec Spec;
            if (!Asc.FindAbilitySpecFromHandle(Handle, Spec))
                continue;
            TArray<UGameplayAbility> Instances = Spec.GetAbilityInstances();
            for (UGameplayAbility Ability : Instances)
            {
                UGameplayAbilityInteractionBase Interaction = Cast<UGameplayAbilityInteractionBase>(Ability);
                if (Interaction == nullptr || Interaction.m_AbilityEnded)
                    continue;
                AInteractiveObjectActor Object = Cast<AInteractiveObjectActor>(Interaction.m_InteractiveActor.Get());
                if (Object != nullptr)
                    return Object;
            }
        }
        return nullptr;
    }
}

class UDsB_BookProbe : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    private float SinceProbe = 0.0;
    private bool bOn = false;
    private bool bJudged = false;
    private bool bLate = false;
    private float StartAt = 0.0;
    private int DoneBefore = -1;
    private int FinBefore = -1;
    private int MovingMax = 0;
    private FString ObjName = "none";
    UPROPERTY()
    private AInteractiveObjectActor Shelf;
    UPROPERTY()
    private TArray<AInteractiveObjectActor> Others;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    private int OthersMoving()
    {
        int N = 0;
        for (AInteractiveObjectActor O : Others)
        {
            if (O == nullptr || O == Shelf || O.GetAnimInstance() == nullptr)
                continue;
            if (O.GetAnimInstance().GetCurrentActiveMontage() != nullptr)
                N++;
        }
        return N;
    }

    private void Begin(AGothicCharacter Hero)
    {
        bOn = true;
        bJudged = false;
        bLate = false;
        StartAt = Gameplay::GetRealTimeSeconds();
        MovingMax = 0;
        Shelf = DsBBooks::UsedObject(Hero);
        ObjName = Shelf != nullptr ? Shelf.GetName().ToString() : "none";
        FString Def = Shelf != nullptr && Shelf.GetInteractiveObjectDefinition() != nullptr
            ? Shelf.GetInteractiveObjectDefinition().GetClass().GetName().ToString() : "none";
        Others.Empty();
        TArray<AInteractiveObjectActor> All;
        GetAllActorsOfClass(AInteractiveObjectActor::StaticClass(), All);
        for (AInteractiveObjectActor O : All)
        {
            if (O != nullptr && DsBBooks::IsShelf(O) && O.ActorLocation.Distance(Hero.ActorLocation) <= DsBBooks::NearCm)
                Others.Add(O);
        }
        DoneBefore = DsBBooks::StepsDone();
        FinBefore = DsBBooks::Finished();
        DsLp::Bus().Emit("book", "start", "book_probe", "obj=" + ObjName + ",def=" + Def + ",near=" + Others.Num()
            + ",done=" + DoneBefore + ",fin=" + FinBefore);
    }

    private void Judge(FString Phase)
    {
        int Done = DsBBooks::StepsDone();
        int Fin = DsBBooks::Finished();
        int Moving = OthersMoving();
        if (Moving > MovingMax)
            MovingMax = Moving;
        FString Res = "unk";
        if (Fin > FinBefore && FinBefore >= 0)
            Res = "solved";
        else if (Done > DoneBefore && DoneBefore >= 0)
            Res = "ok";
        else if ((Done < DoneBefore && Done >= 0) || MovingMax > 0)
            Res = "reset";
        DsLp::Bus().Emit("book", "update", "book_probe", "res=" + Res + ",look=" + Phase + ",done0=" + DoneBefore + ",done=" + Done
            + ",fin0=" + FinBefore + ",fin=" + Fin + ",moving=" + MovingMax + ",obj=" + ObjName);
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        SinceProbe += Delta;
        if (SinceProbe < DsBBooks::ProbeSec)
            return;
        SinceProbe = 0.0;
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr || Hero.Mesh == nullptr || Hero.Mesh.GetAnimInstance() == nullptr)
            return;
        UAnimMontage M = Hero.Mesh.GetAnimInstance().GetCurrentActiveMontage();
        bool bBook = M != nullptr && M.GetName().ToString().Contains("Secret_Door_Book");
        if (bBook && !bOn)
        {
            Begin(Hero);
            return;
        }
        if (!bOn)
            return;
        float T = Gameplay::GetRealTimeSeconds() - StartAt;
        if (T > 1.5)
        {
            int Moving = OthersMoving();
            if (Moving > MovingMax)
                MovingMax = Moving;
        }
        if (!bJudged && T >= DsBBooks::JudgeSec)
        {
            bJudged = true;
            Judge("click");
        }
        else if (bJudged && !bLate && T >= DsBBooks::LateSec)
        {
            bLate = true;
            Judge("late");
        }
        if (!bBook && T > 0.5)
        {
            bOn = false;
            DsLp::Bus().Emit("book", "complete", "book_probe", "obj=" + ObjName);
        }
    }
}
