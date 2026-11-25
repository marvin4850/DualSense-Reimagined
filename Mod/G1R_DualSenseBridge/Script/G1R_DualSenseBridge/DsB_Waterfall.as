// Waterfall probe (mod v46, user 18.11.2026): "the hero stands under a waterfall" -> waterfall:start / waterfall:complete.
// The game has no gameplay state for it: a waterfall is a mesh + an FMOD ambient emitter (SFX_WORLD_Water_Waterfall_<size>_*).
// Every 20 s the emitters whose event name says Waterfall (not Distant) are collected; 4x per second the hero is "under"
// one when he is inside its splash zone (horizontal radius by size, height window), with 150 cm hysteresis on the way out.
// Radius / height are [Г]: the waterfall:update telemetry (d=, dz=) tunes them from the first game test.
namespace DsBFall
{
    const float RescanSec = 20.0;
    const float ProbeSec = 0.25;
    const float ExitSlackCm = 150.0;
    const float MaxDzCm = 900.0;

    float Radius(FString N)
    {
        if (N.Contains("Huge")) return 900.0;
        if (N.Contains("Large")) return 650.0;
        if (N.Contains("Small")) return 300.0;
        return 450.0;
    }

    int Strength(FString N)
    {
        if (N.Contains("Huge")) return 85;
        if (N.Contains("Large")) return 75;
        if (N.Contains("Small")) return 45;
        return 60;
    }
}

class UDsB_WaterfallProbe : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    UPROPERTY()
    private TArray<AActor> Falls;
    private TArray<FString> FallNames;
    private float SinceScan = 1000.0;
    private float SinceProbe = 0.0;
    private bool bUnder = false;
    private int UnderIdx = -1;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        Leave("destruct");
        Falls.Empty();
        FallNames.Empty();
    }

    private void Leave(FString Why)
    {
        if (!bUnder)
            return;
        bUnder = false;
        UnderIdx = -1;
        DsLp::Bus().Emit("waterfall", "complete", "fmod_ambient", "why=" + Why);
    }

    private void Rescan()
    {
        Falls.Empty();
        FallNames.Empty();
        TArray<AFMODAmbientSound> Found;
        GetAllActorsOfClass(AFMODAmbientSound, Found);
        for (AFMODAmbientSound A : Found)
        {
            if (A == nullptr || A.AudioComponent == nullptr || A.AudioComponent.Event == nullptr)
                continue;
            FString N = A.AudioComponent.Event.GetName().ToString();
            if (!N.Contains("Waterfall") || N.Contains("Distant"))
                continue;
            Falls.Add(A);
            FallNames.Add(N);
        }
        DsLp::Bus().Emit("waterfall", "update", "scan", "n=" + Falls.Num() + ",ambient=" + Found.Num());
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr)
        {
            Leave("nohero");
            return;
        }
        SinceScan += Delta;
        SinceProbe += Delta;
        if (SinceScan >= DsBFall::RescanSec)
        {
            SinceScan = 0.0;
            Rescan();
        }
        if (SinceProbe < DsBFall::ProbeSec)
            return;
        SinceProbe = 0.0;
        FVector P = Hero.ActorLocation;
        int Best = -1;
        float BestD = 1000000.0;
        float BestDz = 0.0;
        for (int Idx = 0; Idx < Falls.Num(); Idx++)
        {
            AActor F = Falls[Idx];
            if (F == nullptr || F.IsActorBeingDestroyed())
                continue;
            FVector Q = F.ActorLocation;
            float Dz = Math::Abs(Q.Z - P.Z);
            float D = FVector(Q.X - P.X, Q.Y - P.Y, 0.0).Size();
            float Limit = DsBFall::Radius(FallNames[Idx]) + (bUnder && Idx == UnderIdx ? DsBFall::ExitSlackCm : 0.0);
            if (Dz <= DsBFall::MaxDzCm && D <= Limit && D < BestD)
            {
                Best = Idx;
                BestD = D;
                BestDz = Dz;
            }
        }
        if (Best < 0)
        {
            Leave("out");
            return;
        }
        if (bUnder && Best == UnderIdx)
            return;
        if (bUnder)
            Leave("switch");
        bUnder = true;
        UnderIdx = Best;
        FString N = FallNames[Best];
        DsLp::Bus().Emit("waterfall", "start", "fmod_ambient", "k=" + DsBFall::Strength(N) + ",ev=" + N.Replace(",", " ").Replace("|", " ").Replace(":", " ").Replace("=", " ")
            + ",d=" + Math::RoundToInt(BestD) + ",dz=" + Math::RoundToInt(BestDz));
    }
}
