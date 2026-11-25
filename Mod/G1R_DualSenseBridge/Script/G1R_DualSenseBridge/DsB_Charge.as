// Spell charge probe (mod v51, user 23.11.2026: "show the charge of a spell by vibration, every level; take the ACTUAL charge
// time - a mod may shorten it (Sen-MagicRebalance sets CastTime 0.01 s for the fireball), the vanilla animations are longer").
// Read-only. While the hero has State.Casting, 20x per second the hero's active cast ability (UGameplayAbilityCastSpell) is
// read: its current spell level, the number of levels and the time this level takes. That time is what the game itself waits
// (GA_CastSpell_Default / _Rechargeable: GetCastDuration = section "In" (level 0) or "Transition<N>" + GetLevelCastingTime(),
// the CastTime from the spell config that balance mods change). DsBridge ramps the charge over exactly this time.
//   spellcharge:start    lvl=<0..>,max=<levels>,dur=<ms to the next level / to ready>,itm=<rune or scroll class>
//   spellcharge:update   lvl,max,dur,took=<ms the previous level really took>,itm          (level up)
//   spellcharge:update   full=1,lvl,max,itm                                                    (last level reached and held)
//   spellcharge:complete why=release|cancel|nohero,itm,m=<hero montage now>                     (State.Casting ended)
namespace DsBCharge
{
    const float ProbeSec = 0.05;

    UGameplayAbilityCastSpell ActiveCast(AGothicCharacter Hero)
    {
        UAbilitySystemComponent Asc = Hero.GetAbilitySystemComponent();
        if (Asc == nullptr)
            return nullptr;
        UGameplayAbilityCastSpell Playing = nullptr;
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
                UGameplayAbilityCastSpell Spell = Cast<UGameplayAbilityCastSpell>(Ability);
                if (Spell == nullptr)
                    continue;
                if (Spell.IsCastAbilityActive())
                    return Spell;
                if (Playing == nullptr && Spell.GetCurrentMontage() != nullptr)
                    Playing = Spell;
            }
        }
        return Playing;
    }

    // The game's own GetCastDuration (GA_CastSpell_Default / _Rechargeable / _RechargeableWithoutTransitions): a section
    // that does not exist in the montage has duration 0, so the same formula serves all three.
    float LevelSeconds(UGameplayAbilityCastSpell Spell, int Level)
    {
        float Anim = 0.0;
        if (Level <= 0)
            Anim = Spell.GetCastSectionDuration(n"In");
        else
            Anim = Spell.GetCastSectionDuration(FName("Transition" + Level));
        float Time = Spell.GetLevelCastingTime();
        if (Anim < 0.0)
            Anim = 0.0;
        if (Time < 0.0)
            Time = 0.0;
        return Anim + Time;
    }
}

class UDsB_ChargeProbe : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    private float SinceProbe = 0.0;
    private bool bOn = false;
    private bool bFull = false;
    private int Level = -100;
    private int Levels = 0;
    private float LevelAt = 0.0;
    private float LevelDur = 0.0;
    private FString Item = "none";

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        Stop("destruct");
    }

    private void Stop(FString Why)
    {
        if (!bOn)
            return;
        bOn = false;
        bFull = false;
        Level = -100;
        // m = the hero's montage now: a channel spell (A_Man_Pyrokinesis_Launch_Montage ...) keeps its R2 after the charge
        FString Montage = "none";
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero != nullptr && Hero.Mesh != nullptr && Hero.Mesh.GetAnimInstance() != nullptr
            && Hero.Mesh.GetAnimInstance().GetCurrentActiveMontage() != nullptr)
            Montage = Hero.Mesh.GetAnimInstance().GetCurrentActiveMontage().GetName().ToString();
        DsLp::Bus().Emit("spellcharge", "complete", "cast_ability", "why=" + Why + ",itm=" + Item + ",m=" + Montage);
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        SinceProbe += Delta;
        if (SinceProbe < DsBCharge::ProbeSec)
            return;
        SinceProbe = 0.0;
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr)
        {
            Stop("nohero");
            return;
        }
        if (!Hero.HasGameplayTag(GameplayTag::State_Casting))
        {
            Stop(Hero.HasGameplayTag(GameplayTag::State_Combat_Magic) ? "release" : "cancel");
            return;
        }
        UGameplayAbilityCastSpell Spell = DsBCharge::ActiveCast(Hero);
        if (Spell == nullptr)
            return;
        float Now = Gameplay::GetRealTimeSeconds();
        int Lvl = Spell.GetCurrentSpellLevel();
        if (!bOn)
        {
            bOn = true;
            bFull = false;
            Item = DsMagic::ItemName();
            Levels = Spell.GetSpellLevels();
            Level = Lvl;
            LevelAt = Now;
            LevelDur = DsBCharge::LevelSeconds(Spell, Lvl);
            DsLp::Bus().Emit("spellcharge", "start", "cast_ability", "lvl=" + Lvl + ",max=" + Levels + ",dur="
                + Math::RoundToInt(LevelDur * 1000.0) + ",itm=" + Item);
            return;
        }
        if (Lvl != Level)
        {
            int Took = Math::RoundToInt((Now - LevelAt) * 1000.0);
            Level = Lvl;
            LevelAt = Now;
            LevelDur = DsBCharge::LevelSeconds(Spell, Lvl);
            bFull = false;
            DsLp::Bus().Emit("spellcharge", "update", "cast_ability", "lvl=" + Lvl + ",max=" + Levels + ",dur="
                + Math::RoundToInt(LevelDur * 1000.0) + ",took=" + Took + ",itm=" + Item);
            return;
        }
        if (!bFull && (Levels <= 1 || Spell.IsLastSpellLevel()) && Now - LevelAt >= LevelDur)
        {
            bFull = true;
            DsLp::Bus().Emit("spellcharge", "update", "cast_ability", "full=1,lvl=" + Lvl + ",max=" + Levels + ",itm=" + Item);
        }
    }
}
