// Tactile Lockpicking — what the mod reads from the game: the lock being picked, the hero's lockpicking skill, the
// minigame's plates and the selected plate, the input device. Every read checks for a missing object and comes back empty.
// Reading logic ported from LockPick Helper 2.1.0 Game.as (github.com/tslaceo/G1LockPickHelper, MIT License,
// (c) 2026 tslaceo), prefix renamed to DsLp; the facts it relies on are in LOCKPICK.md §1.2.

// One reading of the minigame's plates (LockPick Helper's FLPH_Reading).
struct FDsLp_Reading
{
    TArray<int> Holes; // holes 1..7 of the settled plates 0..n-1, front to back
    int Cursor = -1;   // the plate the game highlights (-1: none read)
    int Visible = 0;   // how many plates the game shows at the lock
    bool bSettled = false; // false: a plate is moving or the reading failed (Holes then mean nothing)
    TArray<float> Offsets; // DsLp: the raw offset (plate X - bar X) of plates 0..n-1, also while they move; empty if unreadable
}

namespace DsLp
{
    const float HoleTolerance = 0.3; // a plate this close to a hole's centre stands in it

    // Settled offset (plate X minus its bar's X) of hole 1..7, measured in game by LockPick Helper; D moves toward 7.
    float HoleCentre(int Hole)
    {
        if (Hole == 1)
            return -18.5;
        if (Hole == 2)
            return -12.2;
        if (Hole == 3)
            return -6.1;
        if (Hole == 4)
            return 0.0;
        if (Hole == 5)
            return 6.3;
        if (Hole == 6)
            return 12.5;
        return 18.7;
    }

    // The hole 1..7 a plate stands in, or -1 while it is between holes (the game animates every move).
    int HoleOf(float Dx)
    {
        for (int Hole = HoleMin; Hole <= HoleMax; Hole++)
        {
            if (Math::Abs(Dx - HoleCentre(Hole)) < HoleTolerance)
                return Hole;
        }
        return -1;
    }

    // "7 5 4 4" for the log.
    FString HolesText(const TArray<int>& Holes)
    {
        FString Text = "";
        for (int i = 0; i < Holes.Num(); i++)
        {
            if (i > 0)
                Text += " ";
            Text += "" + Holes[i];
        }
        return Text;
    }

    // "75441" for the window title (values there may only hold [A-Za-z0-9_]).
    FString HolesCode(const TArray<int>& Holes)
    {
        FString Text = "";
        for (int i = 0; i < Holes.Num(); i++)
            Text += "" + Holes[i];
        return Text;
    }

    bool SameHoles(const TArray<int>& A, const TArray<int>& B)
    {
        if (A.Num() != B.Num())
            return false;
        for (int i = 0; i < A.Num(); i++)
        {
            if (A[i] != B[i])
                return false;
        }
        return true;
    }
}

namespace DsLpGame
{
    const float LockSearchCm = 400.0; // fallback: the nearest locked object this close to the hero

    AGothicCharacter Hero()
    {
        return Cast<AGothicCharacter>(Gameplay::GetPlayerCharacter(0));
    }

    FName LockOf(AInteractiveObjectActor Object)
    {
        if (Object == nullptr)
            return NAME_None;
        const UInteractiveObjectDefinition Definition = Object.GetInteractiveObjectDefinition();
        if (Definition == nullptr)
            return NAME_None;
        return Definition.m_Lock;
    }

    // The lock of the chest / door the hero is picking: the hero's interaction ability that has not ended and holds an
    // object (OutSource "ability"); without one, the nearest locked object within LockSearchCm ("near"). An object without
    // a lock gives false and no fallback.
    bool FindLock(FName& OutLock, FString& OutSource)
    {
        OutLock = NAME_None;
        OutSource = "none";
        AGothicCharacter Player = Hero();
        if (Player == nullptr)
            return false;

        bool bHeldUnlocked = false;
        UAbilitySystemComponent Abilities = Player.GetAbilitySystemComponent();
        if (Abilities != nullptr)
        {
            TArray<FGameplayAbilitySpecHandle> Handles;
            Abilities.GetAllAbilities(Handles);
            for (FGameplayAbilitySpecHandle Handle : Handles)
            {
                FGameplayAbilitySpec Spec;
                if (!Abilities.FindAbilitySpecFromHandle(Handle, Spec))
                    continue;
                TArray<UGameplayAbility> Instances = Spec.GetAbilityInstances();
                for (UGameplayAbility Ability : Instances)
                {
                    UGameplayAbilityInteractionBase Interaction = Cast<UGameplayAbilityInteractionBase>(Ability);
                    if (Interaction == nullptr || Interaction.m_AbilityEnded)
                        continue;
                    AInteractiveObjectActor Object = Cast<AInteractiveObjectActor>(Interaction.m_InteractiveActor.Get());
                    if (Object == nullptr)
                        continue;
                    FName Lock = LockOf(Object);
                    if (!Lock.IsNone())
                    {
                        OutLock = Lock;
                        OutSource = "ability";
                        return true;
                    }
                    bHeldUnlocked = true;
                }
            }
        }
        if (bHeldUnlocked)
            return false;

        TArray<AInteractiveObjectActor> Objects;
        GetAllActorsOfClass(AInteractiveObjectActor::StaticClass(), Objects);
        float Nearest = LockSearchCm;
        bool bFound = false;
        for (AInteractiveObjectActor Object : Objects)
        {
            if (Object == nullptr)
                continue;
            FName Lock = LockOf(Object);
            if (Lock.IsNone())
                continue;
            float Distance = Object.ActorLocation.Distance(Player.ActorLocation);
            if (Distance > Nearest)
                continue;
            Nearest = Distance;
            OutLock = Lock;
            bFound = true;
        }
        if (bFound)
            OutSource = "near";
        return bFound;
    }

    // LockpickPrecision: 0 untrained, 1 trained, 2 master (the game drops round(precision) connections); 0 when unreadable.
    // The pick's life: how many strains it takes before it breaks (Skill.Lockpicking: Untrained 2, Trained 4, Master 6).
    // Read at the start and polled during the minigame: if the game lowers it on a strain, the mod shows the game's own value.
    float Durability()
    {
        AGothicCharacter Player = Hero();
        if (Player == nullptr)
            return 0.0;
        float32 Value = Player.GetAttributeValue(UAttributeSet_Lockpicking, n"LockpickDurability", 0.0f);
        return Value;
    }

    float Precision()
    {
        AGothicCharacter Player = Hero();
        if (Player == nullptr)
            return 0.0;
        float32 Value = Player.GetAttributeValue(UAttributeSet_Lockpicking, n"LockpickPrecision", 0.0f);
        return Value;
    }

    // The lockpicking skill tag (GE_Skills: Skill.Lockpicking.Untrained / Trained / Master; PlayerCharacter.as maps them to
    // LockpickPrecision 0 / 1 / 2): 0..2, -1 without a tag or a hero.
    int SkillFromTags()
    {
        AGothicCharacter Player = Hero();
        if (Player == nullptr)
            return -1;
        if (Player.HasGameplayTag(GameplayTag::Skill_Lockpicking_Master))
            return 2;
        if (Player.HasGameplayTag(GameplayTag::Skill_Lockpicking_Trained))
            return 1;
        if (Player.HasGameplayTag(GameplayTag::Skill_Lockpicking_Untrained))
            return 0;
        return -1;
    }

    // The game paints the selected plate: HighlightColor (0.65, 0.70, 1.0) instead of white.
    bool IsHighlighted(AGothicLockPieceActor Piece)
    {
        TArray<UActorComponent> Components = Piece.GetComponentsByClass(UPrimitiveComponent::StaticClass());
        for (UActorComponent Component : Components)
        {
            UPrimitiveComponent Primitive = Cast<UPrimitiveComponent>(Component);
            if (Primitive == nullptr)
                continue;
            UMaterialInstance Instance = Cast<UMaterialInstance>(Primitive.GetMaterial(0));
            if (Instance == nullptr)
                continue;
            for (int i = 0; i < Instance.VectorParameterValues.Num(); i++)
            {
                FVectorParameterValue Parameter = Instance.VectorParameterValues[i];
                if (Parameter.ParameterInfo.Name == n"HighlightColor" && Parameter.ParameterValue.R < 0.9)
                    return true;
            }
        }
        return false;
    }

    // One look at the lock scene's pieces (type 1 plate, 2 bar, 3 latch; m_PieceId 0..6) -> DsLp::ReadingOf (DsLp_Plates.as).
    FDsLp_Reading ReadPlates(int Plates)
    {
        TArray<FDsLp_Piece> Pieces;
        if (Plates >= 1 && Plates <= DsLp::MaxPlateIds)
        {
            TArray<AGothicLockPieceActor> Actors;
            GetAllActorsOfClass(AGothicLockPieceActor::StaticClass(), Actors);
            for (AGothicLockPieceActor Actor : Actors)
            {
                if (Actor == nullptr)
                    continue;
                FDsLp_Piece Piece;
                Piece.Kind = int(Actor.m_LockPieceType);
                Piece.Id = Actor.m_PieceId;
                Piece.bHidden = Actor.bHidden;
                USceneComponent Root = Actor.RootComponent;
                if (Root != nullptr)
                {
                    Piece.bHasLocation = true;
                    Piece.Location = Root.GetRelativeLocation();
                }
                if (Piece.Kind == 1 && !Piece.bHidden && Piece.Id >= 0 && Piece.Id < Plates)
                    Piece.bHighlighted = IsHighlighted(Actor);
                Pieces.Add(Piece);
            }
        }
        FDsLp_Reading Result = DsLp::ReadingOf(Pieces, Plates);
        if (Plates < 1 || Plates > DsLp::MaxPlateIds)
            return Result;
        // the continuous offsets (the quantised holes above say nothing while a plate moves)
        TArray<float> PlateX;
        TArray<float> BarX;
        TArray<bool> HasPlate;
        TArray<bool> HasBar;
        PlateX.SetNum(Plates);
        BarX.SetNum(Plates);
        HasPlate.SetNum(Plates);
        HasBar.SetNum(Plates);
        for (int Id = 0; Id < Plates; Id++)
        {
            PlateX[Id] = 0.0;
            BarX[Id] = 0.0;
            HasPlate[Id] = false;
            HasBar[Id] = false;
        }
        for (int i = 0; i < Pieces.Num(); i++)
        {
            int Id = Pieces[i].Id;
            if (Id < 0 || Id >= Plates || !Pieces[i].bHasLocation)
                continue;
            if (Pieces[i].Kind == 1 && !Pieces[i].bHidden && !HasPlate[Id])
            {
                PlateX[Id] = float(Pieces[i].Location.X);
                HasPlate[Id] = true;
            }
            else if (Pieces[i].Kind == 2 && !HasBar[Id])
            {
                BarX[Id] = float(Pieces[i].Location.X);
                HasBar[Id] = true;
            }
        }
        for (int Id = 0; Id < Plates; Id++)
        {
            if (!HasPlate[Id] || !HasBar[Id])
            {
                Result.Offsets.Reset();
                return Result;
            }
            Result.Offsets.Add(PlateX[Id] - BarX[Id]);
        }
        return Result;
    }

    UCommonInputSubsystem InputSubsystem()
    {
        APlayerController Controller = Gameplay::GetPlayerController(0);
        if (Controller == nullptr)
            return nullptr;
        return UCommonInputSubsystem::Get(Controller);
    }

    // Device of the last input: 0 keyboard and mouse, 1 gamepad (2 touch); 0 when it cannot be read.
    int InputType()
    {
        UCommonInputSubsystem Input = InputSubsystem();
        if (Input == nullptr)
            return 0;
        return int(Input.GetCurrentInputType());
    }

    // A game asset by its object path ("/Game/...Name.Name"), loaded now; null when it does not load.
    UObject LoadAsset(FString Path)
    {
        TSoftObjectPtr<UObject> Ref = System::Conv_SoftObjPathToSoftObjRef(System::MakeSoftObjectPath(Path));
        return FAssetPreload::BlockingLoad(Ref);
    }
}
