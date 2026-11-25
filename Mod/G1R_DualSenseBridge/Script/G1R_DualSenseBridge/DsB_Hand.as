// Item in hand (mod v48, user 21.11.2026: "a unique vibration for each kind of swampweed smoked"). The game reports only
// State.Consuming and the montage (Man_Smoke_L_Montage, the same for all three joints); the joint itself is an item visual
// the animation spawns into the hand (notify SpawnItem ~0.45 s) and attaches to the hero. 7x per second the hero's attached
// item visuals are read; a consumable (ItMi_ / ItFo_ / ItPo_ / ItPl_ class: joint, food, potion, plant) in hand is reported:
//   handitem:update itm=<item class>|none
// DsBridge times the smoke effect of ItMi_Joint_01/02/03 from this event (packs/96-world-v48.json).
namespace DsBHand
{
    const float ProbeSec = 0.14;

    bool IsConsumable(FString N)
    {
        return N.StartsWith("UItMi_") || N.StartsWith("UItFo_") || N.StartsWith("UItPo_") || N.StartsWith("UItPl_")
            || N.StartsWith("ItMi_") || N.StartsWith("ItFo_") || N.StartsWith("ItPo_") || N.StartsWith("ItPl_");
    }
}

class UDsB_HandProbe : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    private float SinceProbe = 0.0;
    private FString Last = "none";

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        SinceProbe += Delta;
        if (SinceProbe < DsBHand::ProbeSec)
            return;
        SinceProbe = 0.0;
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr)
            return;
        TArray<AActor> Attached;
        Hero.GetAttachedActors(Attached);
        FString Found = "none";
        for (AActor A : Attached)
        {
            AItemVisual V = Cast<AItemVisual>(A);
            if (V == nullptr || V.Item == nullptr)
                continue;
            FString N = V.Item.GetClass().GetName().ToString();
            if (DsBHand::IsConsumable(N))
            {
                Found = N;
                break;
            }
        }
        if (Found == Last)
            return;
        Last = Found;
        DsLp::Bus().Emit("handitem", "update", "attached", "itm=" + Found);
    }
}
