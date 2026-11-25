// Out of air (mod v47, user 19.11.2026: "air runs out, the screen flashes red, no vibration"). v46 waited for drowning damage
// (hurt:start) - the game test showed none while the screen already flashed: the warning starts at the CRITICAL oxygen level,
// long before damage. The hero's own attribute set is read (UAttributeSet_Oxygen: Oxygen, MaxOxygen, CriticalLevelPercent,
// OxygenDepletionRate - the values the game's oxygen bar shows) 10x per second:
//   oxygen:start  o=,crit=           air fell below the critical level (= the red warning starts)
//   oxygen:update src=beat,n=,o=     one choking beat; the beat gets faster as the air runs out (1.1 s at the critical level
//                                    -> 0.5 s when empty), empty=1 once the air is gone (drowning damage ticks)
//   oxygen:update src=probe,...      telemetry once per dive (max, crit, depletion rate) to tune against the game's flash
//   oxygen:complete why=             air back above the critical level / no hero
// Beat rhythm vs the red flash is [Г] until the game test: the flash period is native UI (UOxygenBarWidget), not readable.
namespace DsBAir
{
    const float ProbeSec = 0.1;
    const float SlowBeatSec = 1.1;
    const float FastBeatSec = 0.5;
    const float EmptyBeatSec = 0.5;
    const float ExitSlack = 0.05;

    int Pct(float V) { return Math::RoundToInt(V * 100.0); }
}

class UDsB_AirProbe : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    private float SinceProbe = 0.0;
    private float SinceBeat = 0.0;
    private bool bLow = false;
    private bool bProbed = false;
    private int Beats = 0;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        Leave("destruct");
    }

    private void Leave(FString Why)
    {
        if (!bLow)
            return;
        bLow = false;
        DsLp::Bus().Emit("oxygen", "complete", "attr_oxygen", "why=" + Why + ",beats=" + Beats);
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        SinceProbe += Delta;
        SinceBeat += Delta;
        if (SinceProbe < DsBAir::ProbeSec)
            return;
        SinceProbe = 0.0;
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr)
        {
            Leave("nohero");
            return;
        }
        float32 O32 = Hero.GetAttributeValue(UAttributeSet_Oxygen, n"Oxygen", -1.0f);
        float32 Max32 = Hero.GetAttributeValue(UAttributeSet_Oxygen, n"MaxOxygen", 0.0f);
        float32 Crit32 = Hero.GetAttributeValue(UAttributeSet_Oxygen, n"CriticalLevelPercent", 0.0f);
        float O = O32;
        float Max = Max32;
        float CritRaw = Crit32;
        if (O < 0.0 || Max <= 0.0)
        {
            Leave("noattr");
            return;
        }
        float Ratio = Math::Clamp(O / Max, 0.0, 1.0);
        float Crit = CritRaw > 1.0 ? CritRaw / 100.0 : CritRaw;
        if (Crit <= 0.01 || Crit >= 0.99)
            Crit = 0.3;
        if (Ratio >= 0.999)
        {
            bProbed = false;
            Leave("full");
            return;
        }
        if (!bProbed)
        {
            bProbed = true;
            float32 Rate32 = Hero.GetAttributeValue(UAttributeSet_Oxygen, n"OxygenDepletionRate", 0.0f);
            float Rate = Rate32;
            DsLp::Bus().Emit("oxygen", "update", "probe", "o=" + DsBAir::Pct(Ratio) + ",max=" + Math::RoundToInt(Max)
                + ",critraw=" + Math::RoundToInt(CritRaw * 100.0) + ",crit=" + DsBAir::Pct(Crit) + ",rate=" + Math::RoundToInt(Rate * 100.0));
        }
        if (!bLow)
        {
            if (Ratio >= Crit)
                return;
            bLow = true;
            Beats = 0;
            SinceBeat = 1000.0;
            DsLp::Bus().Emit("oxygen", "start", "attr_oxygen", "o=" + DsBAir::Pct(Ratio) + ",crit=" + DsBAir::Pct(Crit));
        }
        else if (Ratio >= Crit + DsBAir::ExitSlack)
        {
            Leave("recovered");
            return;
        }
        bool bEmpty = O <= 0.0;
        float Period = bEmpty ? DsBAir::EmptyBeatSec
            : DsBAir::FastBeatSec + (DsBAir::SlowBeatSec - DsBAir::FastBeatSec) * Math::Clamp(Ratio / Crit, 0.0, 1.0);
        if (SinceBeat < Period)
            return;
        SinceBeat = 0.0;
        Beats++;
        DsLp::Bus().Emit("oxygen", "update", "beat", "n=" + Beats + ",o=" + DsBAir::Pct(Ratio) + ",crit=" + DsBAir::Pct(Crit)
            + (bEmpty ? ",empty=1" : ",empty=0"));
    }
}
