// Riding probe (mod v48, v49: rider component, v50: the possessed pawn is the mount while riding; user 21.11.2026: "jump and landing on the scavenger, a bit stronger than the hero's own").
// While the hero rides, his own State.Falling / Landing tags never come (journal 20.11: only Mount / Dismount montages) -
// the MOUNT jumps and falls. 20x per second: the actor the hero is attached to (the mount) and its CharacterMovement.
//   ride:start  mount=<class>           hero sits on a character (attach parent)
//   ride:complete why=                  dismounted / no hero
//   rideair:start vz=<cm/s>,jump=0|1    the mount left the ground (jump = upward speed > 150 cm/s, else it fell off an edge)
//   rideair:complete drop=,net=,ms=     landed: drop = highest point - landing (cm), net = take-off - landing (cm)
namespace DsBRide
{
    const float ProbeSec = 0.05;
    const float JumpVz = 150.0;
}

class UDsB_RideProbe : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    UPROPERTY()
    private ACharacter Mount;
    private float SinceProbe = 0.0;
    private bool bAir = false;
    private float StartZ = 0.0;
    private float MaxZ = 0.0;
    private float AirAt = 0.0;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        Leave("destruct");
    }

    private void Leave(FString Why)
    {
        if (Mount == nullptr)
            return;
        Mount = nullptr;
        bAir = false;
        DsLp::Bus().Emit("ride", "complete", "attach", "why=" + Why);
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        SinceProbe += Delta;
        if (SinceProbe < DsBRide::ProbeSec)
            return;
        SinceProbe = 0.0;
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr)
        {
            Leave("nohero");
            return;
        }
        // v50 (game 21.11, v49 journal: ride:start came at DISMOUNT, ride:complete at MOUNT): while riding the player
        // possesses the mount, so GetPlayerCharacter(0) IS the scavenger. The game itself checks this in GA_Falling:
        // GetMountComponent().GetRiderCharacter(). Then the player pawn is the mount.
        ACharacter Parent = nullptr;
        if (Hero.GetMountComponent() != nullptr && Hero.GetMountComponent().GetRiderCharacter() != nullptr)
            Parent = Hero;
        // v49: the game's own rider component (moment of mounting / dismounting, hero still the pawn)
        if (Parent == nullptr && Hero.GetRiderComponent() != nullptr)
            Parent = Hero.GetRiderComponent().GetMountCharacter();
        if (Parent == nullptr)
            Parent = Cast<ACharacter>(Hero.GetAttachParentActor());
        if (Parent != Mount)
        {
            Leave("dismount");
            if (Parent == nullptr)
                return;
            Mount = Parent;
            bAir = false;
            DsLp::Bus().Emit("ride", "start", "attach", "mount=" + Parent.GetClass().GetName().ToString());
        }
        if (Mount.CharacterMovement == nullptr)
            return;
        bool bFalling = Mount.CharacterMovement.IsFalling();
        float Z = Mount.GetActorLocation().Z;
        float Now = Gameplay::GetRealTimeSeconds();
        if (bFalling && !bAir)
        {
            bAir = true;
            StartZ = Z;
            MaxZ = Z;
            AirAt = Now;
            float Vz = Mount.GetVelocity().Z;
            DsLp::Bus().Emit("rideair", "start", "mount_move", "vz=" + Math::RoundToInt(Vz) + ",jump=" + (Vz > DsBRide::JumpVz ? "1" : "0"));
            return;
        }
        if (bAir && bFalling)
        {
            if (Z > MaxZ)
                MaxZ = Z;
            return;
        }
        if (bAir && !bFalling)
        {
            bAir = false;
            int Drop = Math::Max(0, Math::RoundToInt(MaxZ - Z));
            int Net = Math::Max(0, Math::RoundToInt(StartZ - Z));
            DsLp::Bus().Emit("rideair", "complete", "mount_move", "drop=" + Drop + ",net=" + Net + ",ms=" + Math::RoundToInt((Now - AirAt) * 1000.0));
        }
    }
}
