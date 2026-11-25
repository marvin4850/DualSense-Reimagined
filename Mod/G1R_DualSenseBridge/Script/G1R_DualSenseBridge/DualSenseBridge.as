// G1R DualSense Bridge — Game Event Monitor (v7)
//
// A passive listener ability is added to the player via the official ReplaceParentClass mechanism
// on the player definition effect. It observes real game state (owned gameplay tags, gameplay events
// sent by the damage calculation, the combat data module) and publishes typed events in the window title.
// Nothing in movement, combat, damage, animation, input or saves is changed.
//
// Transport v2 (window title, read-only on the DsBridge side):
//   "Gothic 1 Remake [G1RDS2|<rec>|...]"                    newest record last, up to 8 records (ring; was 4 until v11)
//   <rec> = <seq>:<type>:<phase>:t=<ms>,src=<hook>[,key=value...]
//   seq   = per-session counter (restarts at 1 when the ability is activated again, e.g. after loading)
//   phase = start | update | complete | cancel | interrupt
//   values contain only [A-Za-z0-9_] (no ':' ',' '|' '=')
// The ring lets the companion recover several events raised in the same frame; gaps in seq are detectable.
//
// Events (type:phase  <- source):
//   session:start             <- listener activated (src=ability)
//   mode:update m=world|menu  <- UG1RGameInstance.GetInputMode() + IsGamePaused(), 100 ms poll, on change only
//   air:start                 <- State.Falling added (take-off / walking off a ledge)
//   air:complete drop,net     <- State.Landing added; drop = highest Z of this fall - landing Z (30 ms poll while airborne),
//                                net = take-off Z - landing Z (>= 0; ~0 for a jump in place, the real height for a fall)
//   attack:start dir,node,fam,skill <- new swing: UDataModule_Combat.GetCurrentAttack() changed or State.Attack count rose
//   attack:update res=contact dmg,imp <- Event.Damaged.Target.Before received by the player (player is the source)
//   attack:update res=deflected        <- Action.Deflected received by the player (player's hit deflected)
//   attack:interrupt res=parried       <- Action.Parried received by the player (player's hit parried)
//   attack:complete contact=0|1        <- State.Attack count back to 0 (end of the attack sequence; NOT a proven miss)
//   hurt:start dmg,imp        <- Event.Damaged.Me.Before received by the player (player is the target)
//   parry:start               <- Action.Parry received by the player (player parried; only sent in ParryMode 1)
//   hitreact:start/complete   <- State.Hit tag (hit reaction)
//   dodge:start/complete      <- State.Dodge tag
//   roll:start/complete       <- State.Roll tag
//   weapon:update fam=..      <- State.Combat.* family tags, 100 ms poll, on change only
//   <generated types>         <- generic tag / event hooks from haptics/hooks.json (DualSenseBridge_Generated.as), src=gen_tag / gen_event
//   montage:start/complete m=<asset>[,int=0|1] <- UAnimInstance.OnMontageStarted / OnMontageEnded (every player montage; mod v13)
//   bow:update aim,sec,notch,rel,arrow <- UArcheryComponent probe while the family is bow / crossbow / archery (mod v12-v13)
//   swim:start/complete       <- State.Swimming tag; start carries entry=fall,drop=<cm> when the player fell into the water
//   dive:start/complete       <- State.Movement.Diving tag (the game's own IsDiving()); complete = surfaced
// attack:start also comes from UAnimInstance.OnMontageStarted while State.Attack is held (same button repeated = same
// combo node, the tag stays, so only the montage restart shows the new swing).

UCLASS(meta=(ReplaceParentClass))
class UGE_Player_Definition_DualSenseBridge : UGE_Player_Definition
{
    default AddAbility(UGA_DualSenseBridge_Listener::StaticClass());
}

namespace DsLp
{
    const float32 InsertSec = 0.836f;   // AnimNotify_InsertLockPick in A_Lockpicking_In (2.0 s), from the cooked package
}

class UGA_DualSenseBridge_Listener : UGameplayAbilityCreatureSpecialState
{
    default m_ActivateAbilityOnGranted = true;

    // tag callbacks
    UPROPERTY()
    UDelegateHandleContainer m_FallingHandle;
    UPROPERTY()
    UDelegateHandleContainer m_LandingHandle;
    UPROPERTY()
    UDelegateHandleContainer m_AttackHandle;
    UPROPERTY()
    UDelegateHandleContainer m_DodgeHandle;
    UPROPERTY()
    UDelegateHandleContainer m_RollHandle;
    UPROPERTY()
    UDelegateHandleContainer m_HitHandle;
    UPROPERTY()
    UDelegateHandleContainer m_SwimHandle;
    UPROPERTY()
    UDelegateHandleContainer m_DiveHandle;
    UPROPERTY()
    UAnimInstance m_AnimInst;

    // Container close v1: observe the chest's real montage section, independent of late interaction tags.
    UPROPERTY()
    UAnimMontage m_ChestMontage;
    UPROPERTY()
    FName m_ChestSection;
    UPROPERTY()
    bool m_ChestCloseSent = false;

    // Crafting stations v1 (NH_Interact_*): the station montage is replayed / jumped per section; report each section entry
    // with the montage position, DsBridge schedules the notify-timed effects of that section (packs/75-crafting-test.json).
    UPROPERTY()
    UAnimMontage m_CraftMontage;
    UPROPERTY()
    FName m_CraftSection;
    UPROPERTY()
    float m_CraftLastPos = -1.0f;

    // magic v1 (mod v44, DsB_Magic.as): the current cast / channel montage, its section and the item in hand
    UPROPERTY()
    UAnimMontage m_SpellMontage;
    UPROPERTY()
    FName m_SpellSection;
    UPROPERTY()
    FString m_SpellItem;

    // weapon equipment v2 / loot v1 (16.11.2026)
    UPROPERTY()
    UAnimMontage m_WeaponAnimMontage;
    UPROPERTY()
    int m_LootItems = -1;
    UPROPERTY()
    AActor m_LootTarget;
    UPROPERTY()
    float m_GuardHitAt = -10.0f;

    // generic hooks (DualSenseBridge_Generated.as, from haptics/hooks.json)
    UPROPERTY()
    TArray<UDelegateHandleContainer> m_GenTagHandles;
    UPROPERTY()
    UAbilitySystemComponent m_TagsRegisteredOn;
    UPROPERTY()
    TArray<UAbilityTask_WaitGameplayEvent> m_GenEventTasks;

    // gameplay event callbacks (ability tasks on this ability -> only events sent to the player's own actor)
    UPROPERTY()
    UAbilityTask_WaitGameplayEvent m_TaskDamagedTarget;
    UPROPERTY()
    UAbilityTask_WaitGameplayEvent m_TaskDamagedMe;
    UPROPERTY()
    UAbilityTask_WaitGameplayEvent m_TaskDeflected;
    UPROPERTY()
    UAbilityTask_WaitGameplayEvent m_TaskParried;
    UPROPERTY()
    UAbilityTask_WaitGameplayEvent m_TaskParry;

    // Threat light observer: read-only AI/perception, separate from weapon stance.
    UPROPERTY()
    TArray<AGothicCharacter> m_ThreatActors;
    UPROPERTY()
    float m_ThreatRefreshAt = -1.0f;
    UPROPERTY()
    float m_ThreatSentAt = -1.0f;
    UPROPERTY()
    FString m_ThreatLast = "?";

    // monitor state
    UPROPERTY()
    int m_EventCounter = 0;
    UPROPERTY()
    TArray<FString> m_Ring;
    UPROPERTY()
    int m_LastMode = -1;
    UPROPERTY()
    FString m_LastWeaponFamily = "?";
    UPROPERTY()
    float m_FallMaxZ = 0.0f;
    UPROPERTY()
    float m_FallStartZ = 0.0f;
    UPROPERTY()
    bool m_InAir = false;
    UPROPERTY()
    float m_FallStartTime = 0.0f;
    UPROPERTY()
    FName m_LastAttackName;
    UPROPERTY()
    int m_AttackTagCount = 0;
    UPROPERTY()
    float m_LastAttackSignalTime = -10.0f;
    UPROPERTY()
    bool m_AttackHadContact = false;
    UPROPERTY()
    FString m_LastBowState = "";

    // Tactile Lockpicking: the moment the pick goes in. The game plays FX_Lockpick_Insert from the native
    // AnimNotify_InsertLockPick of A_Lockpicking_In at 0.836 s (package data); scripts cannot see that notify, so the
    // insert is timed on the animation's own clock: State.PickLock added (= the montage enters LoopLock) -> the active
    // montage's position advances by DsLp::InsertSec -> lp:update ph=insert. Fallback without a montage: a game-time timer.
    UPROPERTY()
    UAnimMontage m_LpMontage;
    UPROPERTY()
    bool m_LpInsertPending = false;
    UPROPERTY()
    float m_LpStartPos = 0.0f;
    UPROPERTY()
    float m_LpArmTime = 0.0f;
    UPROPERTY()
    float m_LpWatchUntil = 0.0f;
    UPROPERTY()
    FName m_LpLastSection;

    UFUNCTION(BlueprintOverride)
    void OnActivateAbility_Scriptable(FGameplayAbilityActorInfo GAActorInfo,
                                      FGameplayEventData TriggerEventData)
    {
        UAbilitySystemComponent Asc = GetAbilitySystemComponentFromActorInfo();
        if (Asc == nullptr)
            return;

        m_ChestMontage = nullptr;
        m_ChestSection = NAME_None;
        m_ChestCloseSent = false;

        // Tag subscriptions outlive an activation (the ability is re-activated on the same object after death / load):
        // subscribe only once per ability system, otherwise every tag event is delivered twice (found in session 67).
        bool DsNeedTags = m_TagsRegisteredOn != Asc;
        if (DsNeedTags)
        {
        m_FallingHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Falling, EGameplayTagEventType::NewOrRemoved, this, n"OnFallingTagChanged");
        m_LandingHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Landing, EGameplayTagEventType::NewOrRemoved, this, n"OnLandingTagChanged");
        m_AttackHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Attack, EGameplayTagEventType::AnyCountChange, this, n"OnAttackTagChanged");
        m_DodgeHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Dodge, EGameplayTagEventType::NewOrRemoved, this, n"OnDodgeTagChanged");
        m_RollHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Roll, EGameplayTagEventType::NewOrRemoved, this, n"OnRollTagChanged");
        m_HitHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Hit, EGameplayTagEventType::NewOrRemoved, this, n"OnHitTagChanged");
        m_SwimHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Swimming, EGameplayTagEventType::NewOrRemoved, this, n"OnSwimTagChanged");
        m_DiveHandle = MagicScript::RegisterGameplayTagEvent(Asc, GameplayTag::State_Movement_Diving, EGameplayTagEventType::NewOrRemoved, this, n"OnDiveTagChanged");
        m_GenTagHandles.Empty();
        DsGen::RegisterTags(this, Asc);
        m_TagsRegisteredOn = Asc;
        }
        DsBindAnimInstance();
        // ability tasks end with the previous activation: recreate them every time
        m_GenEventTasks.Empty();
        DsGen::RegisterEvents(this);
        // build id of the generated mod + how many generic hooks were registered (registry status stages 3-4)
        DsEmit("hooks", "update", "register", "build=" + DsGen::BuildId() + ",tags=" + m_GenTagHandles.Num() + ",events=" + m_GenEventTasks.Num() + ",reactivated=" + (DsNeedTags ? 0 : 1));

        m_TaskDamagedTarget = AngelscriptAbilityTask::WaitGameplayEvent(this, GameplayTag::Event_Damaged_Target_Before, nullptr, false, true);
        m_TaskDamagedTarget.EventReceived.AddUFunction(this, n"OnDamagedTarget");
        m_TaskDamagedTarget.ReadyForActivation();
        m_TaskDamagedMe = AngelscriptAbilityTask::WaitGameplayEvent(this, GameplayTag::Event_Damaged_Me_Before, nullptr, false, true);
        m_TaskDamagedMe.EventReceived.AddUFunction(this, n"OnDamagedMe");
        m_TaskDamagedMe.ReadyForActivation();
        m_TaskDeflected = AngelscriptAbilityTask::WaitGameplayEvent(this, GameplayTag::Action_Deflected, nullptr, false, true);
        m_TaskDeflected.EventReceived.AddUFunction(this, n"OnMyHitDeflected");
        m_TaskDeflected.ReadyForActivation();
        m_TaskParried = AngelscriptAbilityTask::WaitGameplayEvent(this, GameplayTag::Action_Parried, nullptr, false, true);
        m_TaskParried.EventReceived.AddUFunction(this, n"OnMyHitParried");
        m_TaskParried.ReadyForActivation();
        m_TaskParry = AngelscriptAbilityTask::WaitGameplayEvent(this, GameplayTag::Action_Parry, nullptr, false, true);
        m_TaskParry.EventReceived.AddUFunction(this, n"OnIParried");
        m_TaskParry.ReadyForActivation();

        DsEmit("session", "start", "ability", "");
        m_ThreatActors.Empty();
        m_ThreatRefreshAt = -1.0f;
        m_ThreatSentAt = -1.0f;
        m_ThreatLast = "?";
        m_LastMode = -1;
        m_LastWeaponFamily = "?";
        System::SetTimer(this, n"PollSlow", 0.1f, true, false, 0.0f, 0.0f);
        System::SetTimer(this, n"PollFast", 0.03f, true, false, 0.0f, 0.0f);
    }

    // ---------------- polling (no callback exists for these) ----------------

    // 100 ms: input mode (world/menu) and weapon family context
    UFUNCTION()
    void PollSlow()
    {
        int DsMode = 0; // 0 = menu, 1 = world
        UG1RGameInstance GameInstance = Cast<UG1RGameInstance>(Gameplay::GetGameInstance());
        if (GameInstance != nullptr && GameInstance.GetInputMode() == EGameInputMode::Game && !Gameplay::IsGamePaused())
            DsMode = 1;
        if (DsMode != m_LastMode)
        {
            m_LastMode = DsMode;
            if (DsMode == 1)
                DsEmit("mode", "update", "inputmode", "m=world");
            else
                DsEmit("mode", "update", "inputmode", "m=menu");
        }

        DsBindAnimInstance();
        AGothicCharacter DsChar = Cast<AGothicCharacter>(GetAvatarActorFromActorInfo());
        if (DsChar == nullptr)
            return;
        DsThreat::Poll(this, DsChar, DsMode == 1);
        FString DsFam = DsWeaponFamily(DsChar);
        if (DsFam != m_LastWeaponFamily)
        {
            m_LastWeaponFamily = DsFam;
            DsEmit("weapon", "update", "tag_StateCombat", "fam=" + DsFam);
        }
    }

    // 30 ms: highest point of the current fall, each new swing (attack config change), bow / crossbow state probe
    UFUNCTION()
    void PollFast()
    {
        AActor PlayerActor = GetAvatarActorFromActorInfo();
        if (PlayerActor == nullptr)
            return;
        if (m_LastWeaponFamily == "bow" || m_LastWeaponFamily == "crossbow" || m_LastWeaponFamily == "archery")
            DsProbeArchery(PlayerActor);
        DsChestSectionPoll();
        DsCraftSectionPoll();
        DsSpellSectionPoll();
        DsLootPoll();
        if (m_LpInsertPending || Gameplay::GetRealTimeSeconds() < m_LpWatchUntil)
            DsLpInsertPoll();
        if (m_InAir)
        {
            float CurZ = float(PlayerActor.GetActorLocation().Z);
            if (CurZ > m_FallMaxZ)
                m_FallMaxZ = CurZ;
        }
        UDataModule_Combat CombatModule = DataModule::GetCombatDataModule(PlayerActor);
        if (CombatModule == nullptr)
            return;
        const UAttackMeleeConfig CurAttack = CombatModule.GetCurrentAttack();
        if (CurAttack == nullptr)
        {
            m_LastAttackName = FName();
            return;
        }
        FName CurAttackName = CurAttack.GetName();
        if (CurAttackName != m_LastAttackName)
        {
            m_LastAttackName = CurAttackName;
            DsSignalAttackStart("poll_CurrentAttack");
        }
    }

    // ---------------- tag callbacks ----------------

    UFUNCTION()
    void OnFallingTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
        {
            AActor PlayerActor = GetAvatarActorFromActorInfo();
            float DsNow = Gameplay::GetRealTimeSeconds();
            // keep the highest point if the tag is re-added during the same fall (stale after 15 s)
            if (PlayerActor != nullptr && (!m_InAir || DsNow - m_FallStartTime > 15.0f))
            {
                m_FallMaxZ = float(PlayerActor.GetActorLocation().Z);
                m_FallStartZ = m_FallMaxZ;
                m_FallStartTime = DsNow;
                m_InAir = true;
            }
            DsEmit("air", "start", "tag_StateFalling", "");
        }
    }

    UFUNCTION()
    void OnLandingTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
        {
            int DropCm = 0;
            int NetCm = 0;
            AActor PlayerActor = GetAvatarActorFromActorInfo();
            if (PlayerActor != nullptr && m_InAir)
            {
                float LandZ = float(PlayerActor.GetActorLocation().Z);
                float DropDiff = m_FallMaxZ - LandZ;
                if (DropDiff > 0.0f)
                    DropCm = Math::RoundToInt(DropDiff);
                float NetDiff = m_FallStartZ - LandZ;
                if (NetDiff > 0.0f)
                    NetCm = Math::RoundToInt(NetDiff);
            }
            m_InAir = false;
            DsEmit("air", "complete", "tag_StateLanding", "drop=" + DropCm + ",net=" + NetCm);
        }
    }

    UFUNCTION()
    void OnAttackTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        // a new application of State.Attack (count went up) = a new swing, even if the config looks the same
        if (NewCount > m_AttackTagCount)
        {
            if (m_AttackTagCount == 0)
                m_AttackHadContact = false;
            System::SetTimer(this, n"OnAttackTagDelayed", 0.03f, false, false, 0.0f, 0.0f);
        }
        else if (NewCount == 0 && m_AttackTagCount > 0)
        {
            if (m_AttackHadContact)
                DsEmit("attack", "complete", "tag_StateAttack", "contact=1");
            else
                DsEmit("attack", "complete", "tag_StateAttack", "contact=0");
            m_AttackHadContact = false;
        }
        m_AttackTagCount = NewCount;
    }

    UFUNCTION()
    void OnAttackTagDelayed()
    {
        DsSignalAttackStart("tag_StateAttack");
    }

    // every montage start; while State.Attack is held a montage (re)start is a new swing
    UFUNCTION()
    void OnAnyMontageStarted(UAnimMontage DsMontage)
    {
        // generic: every montage of the player = an event with the asset name (bow shots, dodges, loots, picks, executions...)
        if (DsMontage != nullptr)
            DsEmit("montage", "start", "anim_MontageStarted", "m=" + DsMontage.GetName().ToString() + ",wf=" + DsBreadth::EquippedFamily() + DsDynamicMontageInfo(DsMontage));
        if (DsMontage != nullptr && DsMontage.GetName() == n"PT_NH_Interact_OpenLockedChest_Montage")
        {
            m_ChestMontage = DsMontage;
            m_ChestSection = NAME_None;
            m_ChestCloseSent = false;
        }
        if (DsMontage != nullptr && (DsMontage.GetName().ToString().Contains("NH_Interact_") || DsMass::Wants(DsMontage.GetName().ToString())))
        {
            m_CraftMontage = DsMontage;
            m_CraftSection = NAME_None;
            m_CraftLastPos = -1.0f;
        }
        if (DsMontage != nullptr && DsMagic::IsMagicMontage(DsMontage.GetName().ToString()))
        {
            m_SpellMontage = DsMontage;
            m_SpellSection = NAME_None;
            m_SpellItem = DsMagic::ItemName();
            DsEmit("spell", "start", "anim_MontageStarted", "m=" + DsMontage.GetName().ToString() + ",itm=" + m_SpellItem + ",pos=0");
        }
        if (m_AttackTagCount > 0)
            System::SetTimer(this, n"OnAttackMontageDelayed", 0.03f, false, false, 0.0f, 0.0f);
        // Weapon equipment v2 (game102): the game's draw/undraw FX fires only ~60 % of the time, so read the real item a moment
        // after a melee draw / put-away montage starts and report it with the montage position (DsBridge times it to the notify).
        if (DsMontage != nullptr)
        {
            FString DsWName = DsMontage.GetName().ToString();
            bool DsWAnim = (DsWName.Contains("_Draw") || DsWName.Contains("_Shelve")) && !DsWName.Contains("T1H") && !DsWName.Contains("Fists")
                && !DsWName.Contains("Bow") && !DsWName.Contains("Crossbow") && !DsWName.Contains("NH_Interact_");
            if (DsWAnim)
            {
                m_WeaponAnimMontage = DsMontage;
                System::SetTimer(this, n"OnWeaponAnimProbe", 0.15f, false, false, 0.0f, 0.0f);
            }
        }
    }

    UFUNCTION()
    void OnWeaponAnimProbe()
    {
        if (m_AnimInst == nullptr || m_WeaponAnimMontage == nullptr || !m_AnimInst.Montage_IsPlaying(m_WeaponAnimMontage))
            return;
        FString DsWName = m_WeaponAnimMontage.GetName().ToString();
        float DsWPos = m_AnimInst.Montage_GetPosition(m_WeaponAnimMontage);
        // game103/104: at 150 ms of a DRAW the item is not "equipped" yet (fam=none every time, it is attached at ~0.3 s), but the
        // combat stance tag of the weapon family is already set: take the stance first, the equipped item only as a fallback.
        FString DsWFam = "none";
        AGothicCharacter DsWChar = Cast<AGothicCharacter>(GetAvatarActorFromActorInfo());
        if (DsWChar != nullptr)
        {
            FString DsStance = DsWeaponFamily(DsWChar);
            if (DsStance == "axe1h") DsWFam = "a1h";
            else if (DsStance == "axe2h") DsWFam = "a2h";
            else if (DsStance == "mace1h") DsWFam = "m1h";
            else if (DsStance == "mace2h") DsWFam = "m2h";
            else if (DsStance == "s1h" || DsStance == "s2h" || DsStance == "staff" || DsStance == "torch") DsWFam = DsStance;
        }
        if (DsWFam == "none")
            DsWFam = DsBreadth::EquippedFamily();
        DsEmit("weaponanim", "update", "draw_probe", "act=" + (DsWName.Contains("_Draw") ? "draw" : "sheathe") + ",m=" + DsWName
            + ",fam=" + DsWFam + ",pos=" + Math::RoundToInt(DsWPos * 1000.0f));
    }

    // Loot v2 (user, 16.11.2026: watch the CONTAINER; one coin out of a stack of 100 must count too).
    // While the hero interacts (State.Interact), the target of the active interaction ability is found once; every 30 ms:
    //   body  (a character): the total AMOUNT in its inventory (CountItemsOfClass per item kind) - a drop = something taken;
    //   chest (anything else): the chest slot amounts are not visible to scripts (game: 'm_Amount is not a member'), so the
    //         other side is watched - the hero's total amount grows by exactly what was taken (ore nuggets are items too).
    // loottake:update n=<pieces taken>,kind=body|chest
    void DsLootPoll()
    {
        AGothicCharacter DsHero = Cast<AGothicCharacter>(GetAvatarActorFromActorInfo());
        if (DsHero == nullptr || !DsHero.HasGameplayTag(GameplayTag::State_Interact))
        {
            m_LootTarget = nullptr;
            m_LootItems = -1;
            return;
        }
        if (m_LootTarget == nullptr)
        {
            m_LootTarget = DsInteractionTarget(DsHero);
            m_LootItems = -1;
            if (m_LootTarget == nullptr)
                return;
        }
        AGothicCharacter DsBody = Cast<AGothicCharacter>(m_LootTarget);
        // chest mode only for real containers: stations / crafting also add items to the hero (forge: the finished blade)
        if (DsBody == nullptr && DataModule::GetContainerDataModule(m_LootTarget) == nullptr)
            return;
        int DsTotal = DsInventoryAmount(DsBody != nullptr ? DsBody : DsHero);
        if (DsTotal < 0)
            return;
        if (m_LootItems >= 0)
        {
            int DsTaken = DsBody != nullptr ? m_LootItems - DsTotal : DsTotal - m_LootItems;
            if (DsTaken > 0)
                DsEmit("loottake", "update", "container", "n=" + DsTaken + ",kind=" + (DsBody != nullptr ? "body" : "chest"));
        }
        m_LootItems = DsTotal;
    }

    AActor DsInteractionTarget(AGothicCharacter DsHero)
    {
        UAbilitySystemComponent DsAsc = DsHero.GetAbilitySystemComponent();
        if (DsAsc == nullptr)
            return nullptr;
        TArray<FGameplayAbilitySpecHandle> DsHandles;
        DsAsc.GetAllAbilities(DsHandles);
        for (FGameplayAbilitySpecHandle DsHandle : DsHandles)
        {
            FGameplayAbilitySpec DsSpec;
            if (!DsAsc.FindAbilitySpecFromHandle(DsHandle, DsSpec))
                continue;
            TArray<UGameplayAbility> DsInst = DsSpec.GetAbilityInstances();
            for (UGameplayAbility DsAbility : DsInst)
            {
                UGameplayAbilityInteractionBase DsInter = Cast<UGameplayAbilityInteractionBase>(DsAbility);
                if (DsInter != nullptr && !DsInter.m_AbilityEnded && DsInter.m_InteractiveActor.Get() != nullptr)
                    return DsInter.m_InteractiveActor.Get();
            }
        }
        return nullptr;
    }

    int DsInventoryAmount(AGothicCharacter DsOwner)
    {
        if (DsOwner == nullptr || DsOwner.GetInventory() == nullptr)
            return -1;
        int DsSum = 0;
        for (UItemDefinition DsItem : DsOwner.GetInventory().GetItems())
        {
            if (DsItem != nullptr)
                DsSum += DsOwner.GetInventory().CountItemsOfClass(TSubclassOf<UItemDefinition>(DsItem.GetClass()));
        }
        return DsSum;
    }

    UFUNCTION()
    void OnAnyMontageEnded(UAnimMontage DsMontage, bool bInterrupted)
    {
        if (DsMontage == nullptr)
            return;
        if (DsMontage == m_ChestMontage)
        {
            if (m_ChestCloseSent)
                DsEmit("containerlid", bInterrupted ? "cancel" : "complete", "montage_ended", "kind=close");
            m_ChestMontage = nullptr;
            m_ChestSection = NAME_None;
            m_ChestCloseSent = false;
        }
        if (bInterrupted)
            DsEmit("montage", "complete", "anim_MontageEnded", "m=" + DsMontage.GetName().ToString() + ",int=1");
        else
            DsEmit("montage", "complete", "anim_MontageEnded", "m=" + DsMontage.GetName().ToString() + ",int=0");
        // magic v1: every cast / channel montage that ends (a failure montage interrupting the cast ends the cast too)
        if (DsMagic::IsMagicMontage(DsMontage.GetName().ToString()))
        {
            DsEmit("spell", "complete", "anim_MontageEnded", "m=" + DsMontage.GetName().ToString() + ",int=" + (bInterrupted ? 1 : 0));
            if (DsMontage == m_SpellMontage)
            {
                m_SpellMontage = nullptr;
                m_SpellSection = NAME_None;
            }
        }
    }

    // magic v1: one spell:update per section NAME change of the cast / channel montage (30 ms poll). Loop wraps of the same
    // section are not reported: the hold effect keeps playing until the montage leaves the section.
    void DsSpellSectionPoll()
    {
        if (m_AnimInst == nullptr || m_SpellMontage == nullptr)
            return;
        if (!m_AnimInst.Montage_IsPlaying(m_SpellMontage))
            return;
        FName Section = m_AnimInst.Montage_GetCurrentSection(m_SpellMontage);
        if (Section == m_SpellSection)
            return;
        bool First = m_SpellSection == NAME_None;
        m_SpellSection = Section;
        if (First)
            return;   // the opening section is spell:start itself
        float Position = m_AnimInst.Montage_GetPosition(m_SpellMontage);
        DsEmit("spell", "update", "montage_section", "m=" + m_SpellMontage.GetName().ToString() + ",itm=" + m_SpellItem
            + ",sec=" + Section.ToString() + ",pos=" + Math::RoundToInt(Position * 1000.0f));
    }

    UFUNCTION()
    void OnAttackMontageDelayed()
    {
        AActor PlayerActor = GetAvatarActorFromActorInfo();
        if (PlayerActor == nullptr)
            return;
        UDataModule_Combat CombatModule = DataModule::GetCombatDataModule(PlayerActor);
        // a montage restart is a NEW swing only if the last swing started >= 0.6 s ago: one swing plays 1-2 extra montages
        // 0.1-0.5 s after its start (sessions 63/67/71), real repeats come >= 0.6 s apart
        if (CombatModule != nullptr && CombatModule.GetCurrentAttack() != nullptr && Gameplay::GetRealTimeSeconds() - m_LastAttackSignalTime >= 0.6f)
            DsSignalAttackStart("anim_MontageStarted");
    }

    void DsBindAnimInstance()
    {
        ACharacter DsCharacter = Cast<ACharacter>(GetAvatarActorFromActorInfo());
        if (DsCharacter == nullptr || DsCharacter.Mesh == nullptr)
            return;
        UAnimInstance DsAnim = DsCharacter.Mesh.GetAnimInstance();
        if (DsAnim == nullptr || DsAnim == m_AnimInst)
            return;
        m_AnimInst = DsAnim;
        m_AnimInst.OnMontageStarted.AddUFunction(this, n"OnAnyMontageStarted");
        m_AnimInst.OnMontageEnded.AddUFunction(this, n"OnAnyMontageEnded");
    }

    UFUNCTION()
    void OnSwimTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
        {
            FString DsCtx = "entry=walk";
            AActor PlayerActor = GetAvatarActorFromActorInfo();
            if (m_InAir && PlayerActor != nullptr)
            {
                float DropDiff = m_FallMaxZ - float(PlayerActor.GetActorLocation().Z);
                int DropCm = 0;
                if (DropDiff > 0.0f)
                    DropCm = Math::RoundToInt(DropDiff);
                DsCtx = "entry=fall,drop=" + DropCm;
                m_InAir = false;
            }
            DsEmit("swim", "start", "tag_StateSwimming", DsCtx);
        }
        else
            DsEmit("swim", "complete", "tag_StateSwimming", "");
    }

    UFUNCTION()
    void OnDiveTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
            DsEmit("dive", "start", "tag_StateMovementDiving", "");
        else
            DsEmit("dive", "complete", "tag_StateMovementDiving", "");
    }

    UFUNCTION()
    void OnDodgeTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
            DsEmit("dodge", "start", "tag_StateDodge", "");
        else
            DsEmit("dodge", "complete", "tag_StateDodge", "");
    }

    UFUNCTION()
    void OnRollTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
            DsEmit("roll", "start", "tag_StateRoll", "");
        else
            DsEmit("roll", "complete", "tag_StateRoll", "");
    }

    UFUNCTION()
    void OnHitTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (NewCount > 0)
        {
            // game108: a blocked blow also raises State.Hit; flag it so the heavy body shove does not bury the clash of the block
            AGothicCharacter DsHHero = Cast<AGothicCharacter>(GetAvatarActorFromActorInfo());
            // blk=2: the blocked blow was already reported (hurt with blk=1, it carries the weapons) - the hit stays silent;
            // blk=1: only the block tag, no hurt (game109) - DsBridge plays the clash by the hero's own weapon; blk=0: a real hit
            bool DsHurtSeen = Gameplay::GetRealTimeSeconds() - m_GuardHitAt < 0.15f;
            bool DsGuard = DsHHero != nullptr && DsHHero.HasGameplayTag(GameplayTag::State_Block);
            DsEmit("hitreact", "start", "tag_StateHit", DsHurtSeen ? "blk=2" : (DsGuard ? "blk=1" : "blk=0"));
        }
        else
            DsEmit("hitreact", "complete", "tag_StateHit", "");
    }

    // ---------------- gameplay event callbacks (UGE_Ex_Damage) ----------------

    UFUNCTION()
    void OnDamagedTarget(FGameplayEventData EvData)
    {
        m_AttackHadContact = true;
        DsEmit("attack", "update", "ev_DamagedTargetBefore", "res=contact,dmg=" + Math::RoundToInt(EvData.EventMagnitude) + DsImpact(EvData) + DsCombatCtx(EvData) + DsMagic::DamageCtx());
    }

    UFUNCTION()
    void OnDamagedMe(FGameplayEventData EvData)
    {
        AGothicCharacter DsGHero = Cast<AGothicCharacter>(GetAvatarActorFromActorInfo());
        if (DsGHero != nullptr && DsGHero.HasGameplayTag(GameplayTag::State_Block))
            m_GuardHitAt = Gameplay::GetRealTimeSeconds();
        DsEmit("hurt", "start", "ev_DamagedMeBefore", "dmg=" + Math::RoundToInt(EvData.EventMagnitude) + DsImpact(EvData) + DsCombatCtx(EvData));
    }

    UFUNCTION()
    void OnMyHitDeflected(FGameplayEventData EvData)
    {
        DsEmit("attack", "update", "ev_ActionDeflected", "res=deflected" + DsCombatCtx(EvData));
    }

    UFUNCTION()
    void OnMyHitParried(FGameplayEventData EvData)
    {
        DsEmit("attack", "interrupt", "ev_ActionParried", "res=parried" + DsImpact(EvData) + DsCombatCtx(EvData));
    }

    UFUNCTION()
    void OnIParried(FGameplayEventData EvData)
    {
        DsEmit("parry", "start", "ev_ActionParry", DsImpactBare(EvData) + DsCombatCtx(EvData));
    }

    // ---------------- generic hooks (tables generated by tools/gen_hooks.py) ----------------

    void DsAddGenericTag(UAbilitySystemComponent Asc, FGameplayTag Tag)
    {
        m_GenTagHandles.Add(MagicScript::RegisterGameplayTagEvent(Asc, Tag, EGameplayTagEventType::NewOrRemoved, this, n"OnGenericTagChanged"));
    }

    UFUNCTION()
    void OnGenericTagChanged(const FGameplayTag CallbackTag, int32 NewCount)
    {
        if (CallbackTag == GameplayTag::State_PickLock && NewCount > 0)
            DsLpInsertArm();
        FString DsType = DsGen::TypeFor(CallbackTag);
        if (DsType == "")
            return;
        if (NewCount > 0)
            DsEmit(DsType, "start", "gen_tag", "");
        else
            DsEmit(DsType, "complete", "gen_tag", "");
    }

    void DsAddGenericEvent(FGameplayTag Tag)
    {
        UAbilityTask_WaitGameplayEvent DsTask = AngelscriptAbilityTask::WaitGameplayEvent(this, Tag, nullptr, false, true);
        DsTask.EventReceived.AddUFunction(this, n"OnGenericEvent");
        DsTask.ReadyForActivation();
        m_GenEventTasks.Add(DsTask);
    }

    UFUNCTION()
    void OnGenericEvent(FGameplayEventData EvData)
    {
        FString DsType = DsGen::TypeFor(EvData.EventTag);
        if (DsType == "")
            return;
        DsEmit(DsType, "start", "gen_event", "mag=" + Math::RoundToInt(EvData.EventMagnitude));
    }

    // Bow / crossbow state probe: the draw itself is native (no script hook), so the archery component's public flags are
    // read every 30 ms while a bow / crossbow is the active family and reported on change:
    //   bow:update aim=IsAiming sec=IsSecureToFire notch=IsNotchingArrowOntoBow rel=IsWaitingToFinishRelease arrow=HasArrowNotched
    void DsProbeArchery(AActor PlayerActor)
    {
        UArcheryComponent DsArch = Cast<UArcheryComponent>(PlayerActor.GetComponentByClass(TSubclassOf<UActorComponent>(UArcheryComponent::StaticClass())));
        if (DsArch == nullptr)
            return;
        int DsArrow = 0;
        AArcheryWeaponVisual DsBow = DsArch.GetBowOrCrossbowWeapon();
        if (DsBow != nullptr && DsBow.HasArrowNotched())
            DsArrow = 1;
        FString DsState = "aim=" + (DsArch.IsAiming() ? 1 : 0) + ",sec=" + (DsArch.IsSecureToFire() ? 1 : 0) + ",notch=" + (DsArch.IsNotchingArrowOntoBow() ? 1 : 0)
                        + ",rel=" + (DsArch.IsWaitingToFinishRelease() ? 1 : 0) + ",arrow=" + DsArrow;
        // v45: the string's real draw progress (UBipedRangedCombatAnimInstance.m_DrawingStringAlpha, read-only):
        // draw=1 while the hero pulls, full=1 once the string is fully drawn - the "shot is charged" moment
        UBipedRangedCombatAnimInstance DsRanged = DsRangedAnim();
        float DsAlpha = DsRanged != nullptr ? DsRanged.m_DrawingStringAlpha : -1.0f;
        if (DsRanged != nullptr)
            DsState += ",draw=" + (DsRanged.m_IsDrawing ? 1 : 0) + ",full=" + (DsRanged.m_IsDrawing && DsAlpha >= 0.98f ? 1 : 0);
        else
            DsState += ",draw=na";
        if (DsState != m_LastBowState)
        {
            m_LastBowState = DsState;
            DsEmit("bow", "update", "probe_ArcheryComponent", DsState + ",alpha=" + Math::RoundToInt(DsAlpha * 100.0f));
        }
    }

    // the ranged-combat anim layer of the hero: the main anim instance, a linked layer of that class, or any linked instance
    UBipedRangedCombatAnimInstance DsRangedAnim()
    {
        ACharacter DsC = Cast<ACharacter>(GetAvatarActorFromActorInfo());
        if (DsC == nullptr || DsC.Mesh == nullptr)
            return nullptr;
        UAnimInstance DsMain = DsC.Mesh.GetAnimInstance();
        UBipedRangedCombatAnimInstance DsR = Cast<UBipedRangedCombatAnimInstance>(DsMain);
        if (DsR != nullptr)
            return DsR;
        if (DsMain != nullptr)
        {
            DsR = Cast<UBipedRangedCombatAnimInstance>(DsMain.GetLinkedAnimLayerInstanceByClass(TSubclassOf<UAnimInstance>(UBipedRangedCombatAnimInstance::StaticClass()), true));
            if (DsR != nullptr)
                return DsR;
        }
        for (UAnimInstance DsL : DsC.Mesh.GetLinkedAnimInstances())
        {
            DsR = Cast<UBipedRangedCombatAnimInstance>(DsL);
            if (DsR != nullptr)
                return DsR;
        }
        return nullptr;
    }

    // Entering Out is the closing animation itself. Loop/LoopIdle/LoopLock/Unlock and Failed_Out never fire a close.
    // Track the stored montage even if an overlay becomes the current active montage. One start per montage activation.
    void DsChestSectionPoll()
    {
        if (m_AnimInst == nullptr || m_ChestMontage == nullptr)
            return;
        if (!m_AnimInst.Montage_IsPlaying(m_ChestMontage))
        {
            if (m_ChestCloseSent)
                DsEmit("containerlid", "complete", "montage_stopped", "kind=close");
            m_ChestMontage = nullptr;
            m_ChestSection = NAME_None;
            m_ChestCloseSent = false;
            return;
        }
        FName Section = m_AnimInst.Montage_GetCurrentSection(m_ChestMontage);
        if (Section == m_ChestSection)
            return;
        m_ChestSection = Section;
        float Position = m_AnimInst.Montage_GetPosition(m_ChestMontage);
        DsEmit("containeranim", "update", "montage_section", "sec=" + Section.ToString() + ",pos=" + Math::RoundToInt(Position * 1000.0f));
        if (Section == n"Out" && !m_ChestCloseSent)
        {
            m_ChestCloseSent = true;
            DsEmit("containerlid", "start", "montage_Out", "kind=close,pos=" + Math::RoundToInt(Position * 1000.0f));
        }
    }

    // Diagnostic (wave 2, 16.11.2026): 56 % of the hero's montage starts are runtime montages "AnimMontage_<n>" built from a
    // plain animation; report which animation, so a later wave can time effects by the sequence's own notifies.
    FString DsDynamicMontageInfo(UAnimMontage DsMontage)
    {
        if (DsMontage == nullptr || !DsMontage.GetName().ToString().Contains("AnimMontage_"))
            return "";
        UAnimSequenceBase DsRef = DsMontage.GetFirstAnimReference();
        FString DsAnim = DsRef != nullptr ? DsRef.GetName().ToString() : "none";
        return ",anim=" + DsAnim + ",len=" + Math::RoundToInt(DsMontage.GetPlayLength() * 1000.0f);
    }

    // One craftanim:update per section entry (new section name, a replay, or a jump back in time).
    void DsCraftSectionPoll()
    {
        if (m_AnimInst == nullptr || m_CraftMontage == nullptr)
            return;
        if (!m_AnimInst.Montage_IsPlaying(m_CraftMontage))
        {
            m_CraftMontage = nullptr;
            m_CraftSection = NAME_None;
            m_CraftLastPos = -1.0f;
            return;
        }
        FName Section = m_AnimInst.Montage_GetCurrentSection(m_CraftMontage);
        float Position = m_AnimInst.Montage_GetPosition(m_CraftMontage);
        bool Entered = Section != m_CraftSection || m_CraftLastPos < 0.0f || Position < m_CraftLastPos - 0.05f;
        m_CraftSection = Section;
        m_CraftLastPos = Position;
        if (Entered)
            DsEmit("craftanim", "update", "montage_section", "m=" + m_CraftMontage.GetName().ToString() + ",sec=" + Section.ToString() + ",pos=" + Math::RoundToInt(Position * 1000.0f));
    }

    // ---------------- Tactile Lockpicking: pick insertion ----------------

    void DsLpInsertArm()
    {
        float DsNow = Gameplay::GetRealTimeSeconds();
        m_LpInsertPending = true;
        m_LpArmTime = DsNow;
        m_LpWatchUntil = DsNow + 4.0f;
        m_LpMontage = nullptr;
        m_LpLastSection = NAME_None;
        DsBindAnimInstance();
        if (m_AnimInst != nullptr)
            m_LpMontage = m_AnimInst.GetCurrentActiveMontage();
        if (m_LpMontage != nullptr)
        {
            m_LpStartPos = m_AnimInst.Montage_GetPosition(m_LpMontage);
            m_LpLastSection = m_AnimInst.Montage_GetCurrentSection(m_LpMontage);
            DsLpLog(f"insert armed by State.PickLock: montage {m_LpMontage.GetName()} section {m_LpLastSection} position {m_LpStartPos}, insert at +{DsLp::InsertSec} s of the animation");
        }
        else
        {
            DsLpLog("insert armed by State.PickLock: no active montage -> game-time timer");
            System::SetTimer(this, n"DsLpInsertTimer", DsLp::InsertSec, false, false, 0.0f, 0.0f);
        }
    }

    // every 30 ms while armed (and 4 s of section logging, temporary instrumentation)
    void DsLpInsertPoll()
    {
        float DsNow = Gameplay::GetRealTimeSeconds();
        if (m_AnimInst == nullptr || m_LpMontage == nullptr)
            return;
        bool bPlaying = m_AnimInst.Montage_IsPlaying(m_LpMontage);
        float DsPos = bPlaying ? m_AnimInst.Montage_GetPosition(m_LpMontage) : -1.0f;
        FName DsSection = bPlaying ? m_AnimInst.Montage_GetCurrentSection(m_LpMontage) : NAME_None;
        if (DsSection != m_LpLastSection)
        {
            m_LpLastSection = DsSection;
            DsLpLog(f"montage section {DsSection} at position {DsPos}, {Math::RoundToInt((DsNow - m_LpArmTime) * 1000.0f)} ms after State.PickLock");
        }
        if (!m_LpInsertPending)
            return;
        float DsAdvance = DsPos - m_LpStartPos;
        if (!bPlaying)
            DsLpInsertFire("montage_ended", DsNow);
        else if (DsAdvance >= DsLp::InsertSec || DsAdvance < -0.05f)   // reached, or the section looped back past it
            DsLpInsertFire("montage", DsNow);
        else if (DsNow - m_LpArmTime > 2.5f)
            DsLpInsertFire("timeout", DsNow);
    }

    UFUNCTION()
    void DsLpInsertTimer()
    {
        if (m_LpInsertPending)
            DsLpInsertFire("timer", Gameplay::GetRealTimeSeconds());
    }

    void DsLpInsertFire(FString DsVia, float DsNow)
    {
        m_LpInsertPending = false;
        int DsMs = Math::RoundToInt((DsNow - m_LpArmTime) * 1000.0f);
        DsLpLog(f"PICK INSERTED ({DsVia}): {DsMs} ms after State.PickLock");
        DsEmit("lp", "update", "anim_insert", "ph=insert,via=" + DsVia + ",ms=" + DsMs);
    }

    void DsLpLog(FString DsText)
    {
        UDsLp_Bus DsBus = DsLp::Bus();
        if (DsBus != nullptr && DsBus.m_Log != nullptr)
            DsBus.m_Log.Line(DsText);
    }

    // ---------------- helpers ----------------

    void DsSignalAttackStart(FString DsSrc)
    {
        float DsNow = Gameplay::GetRealTimeSeconds();
        if (DsNow - m_LastAttackSignalTime < 0.12f)
            return; // the same swing seen by both the tag and the config poll
        m_LastAttackSignalTime = DsNow;

        FString DsCtx = "";
        AActor PlayerActor = GetAvatarActorFromActorInfo();
        if (PlayerActor != nullptr)
        {
            UDataModule_Combat CombatModule = DataModule::GetCombatDataModule(PlayerActor);
            if (CombatModule != nullptr)
            {
                FGameplayTag AttackDir = CombatModule.GetCurrentAttackDirection();
                if (AttackDir == GameplayTag::Combat_Attack_Top || AttackDir == GameplayTag::Combat_Attack_TopLeft || AttackDir == GameplayTag::Combat_Attack_TopRight)
                    DsCtx = "dir=top";
                else if (AttackDir == GameplayTag::Combat_Attack_Bottom || AttackDir == GameplayTag::Combat_Attack_BottomLeft || AttackDir == GameplayTag::Combat_Attack_BottomRight)
                    DsCtx = "dir=bottom";
                else if (AttackDir == GameplayTag::Combat_Attack_Left)
                    DsCtx = "dir=left";
                else if (AttackDir == GameplayTag::Combat_Attack_Right)
                    DsCtx = "dir=right";
                else if (AttackDir == GameplayTag::Combat_Attack_Kick)
                    DsCtx = "dir=kick";
                else
                    DsCtx = "dir=unknown";
                const UAttackMeleeConfig CurAttack = CombatModule.GetCurrentAttack();
                if (CurAttack != nullptr)
                    DsCtx = DsCtx + ",node=" + CurAttack.GetName().ToString();
            }
            AGothicCharacter DsChar = Cast<AGothicCharacter>(PlayerActor);
            if (DsChar != nullptr)
            {
                FString DsFam = DsWeaponFamily(DsChar);
                DsCtx = DsCtx + ",fam=" + DsFam;
                FString DsSkill = DsSkillTier(DsChar, DsFam);
                if (DsSkill != "")
                    DsCtx = DsCtx + ",skill=" + DsSkill;
            }
        }
        DsEmit("attack", "start", DsSrc, DsCtx);
    }

    // weapon family from the owned State.Combat.* tags; "none" = no combat stance tag, "unknown" = stance without a known family
    FString DsWeaponFamily(AGothicCharacter DsChar)
    {
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Fist)) return "fist";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Sword_OneHanded)) return "s1h";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Sword_TwoHanded)) return "s2h";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Axe_OneHanded)) return "axe1h";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Axe_TwoHanded)) return "axe2h";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Mace_OneHanded)) return "mace1h";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Mace_TwoHanded)) return "mace2h";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Staff)) return "staff";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Torch)) return "torch";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee_Pickaxe)) return "pickaxe";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Ranged_Bow)) return "bow";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Ranged_Crossbow)) return "crossbow";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Archery)) return "archery";   // the player's bow / crossbow stance (session 71)
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Magic_Rune)) return "rune";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Magic_Scroll)) return "scroll";
        if (DsChar.HasGameplayTag(GameplayTag::State_Combat_Melee) || DsChar.HasGameplayTag(GameplayTag::State_Combat_Ranged) || DsChar.HasGameplayTag(GameplayTag::State_Combat_Magic)) return "unknown";
        return "none";
    }

    // skill tier for the family's weapon class (Skill.* tags); "" when not applicable
    FString DsSkillTier(AGothicCharacter DsChar, FString DsFam)
    {
        if (DsFam == "s1h" || DsFam == "axe1h" || DsFam == "mace1h")
        {
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Melee_OneHanded_Master)) return "master";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Melee_OneHanded_Trained)) return "trained";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Melee_OneHanded_Untrained)) return "untrained";
            return "unknown";
        }
        if (DsFam == "s2h" || DsFam == "axe2h" || DsFam == "mace2h" || DsFam == "staff")
        {
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Melee_TwoHanded_Master)) return "master";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Melee_TwoHanded_Trained)) return "trained";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Melee_TwoHanded_Untrained)) return "untrained";
            return "unknown";
        }
        if (DsFam == "bow")
        {
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Ranged_Bow_Master)) return "master";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Ranged_Bow_Trained)) return "trained";
            return "untrained"; // GA_ArcheryBow treats missing tags as untrained
        }
        if (DsFam == "crossbow")
        {
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Ranged_Crossbow_Master)) return "master";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Ranged_Crossbow_Trained)) return "trained";
            if (DsChar.HasGameplayTag(GameplayTag::Skill_Ranged_Crossbow_Untrained)) return "untrained";
            return "unknown";
        }
        return "";
    }

    // Combat matrix v2 (16.11.2026): who hits what with what, on the RELIABLE gameplay events (game104: the impact FX came for only
    // 5 of 11 contacts). The other side of the event = the opponent. wpn = attacker's weapon, def = defender's weapon,
    // tmat/tsize = what is hit, amat/asize = who hits (DsB_CombatFX.as: stone|ice|bone|zombie|chitin|creature|metal|leather|flesh).
    FString DsCombatCtx(FGameplayEventData EvData)
    {
        AGothicCharacter DsHero = Cast<AGothicCharacter>(GetAvatarActorFromActorInfo());
        AGothicCharacter DsA = Cast<AGothicCharacter>(EvData.Instigator);
        AGothicCharacter DsT = Cast<AGothicCharacter>(EvData.Target);
        AGothicCharacter DsOther = DsT != nullptr && DsT != DsHero ? DsT : DsA;
        if (DsOther == DsHero) DsOther = nullptr;
        bool DsHeroAttacks = DsA == DsHero || (DsA == nullptr && DsT != DsHero);
        AGothicCharacter DsAtk = DsHeroAttacks ? DsHero : DsOther;
        AGothicCharacter DsTgt = DsHeroAttacks ? DsOther : DsHero;
        return ",wpn=" + DsCombat::Family(DsAtk) + ",def=" + DsCombat::Family(DsTgt) + ",tmat=" + DsCombat::Material(DsTgt)
            + ",tsize=" + DsCombat::Size(DsTgt) + ",amat=" + DsCombat::Material(DsAtk) + ",asize=" + DsCombat::Size(DsAtk)
            + ",wmat=" + DsCombat::ClashMat(DsAtk, DsTgt) + ",blk=" + (DsHero != nullptr && DsHero.HasGameplayTag(GameplayTag::State_Block) ? 1 : 0);
    }

    // ",imp=<hit type>" from the effect context of the event
    FString DsImpact(FGameplayEventData EvData)
    {
        return "," + DsImpactBare(EvData);
    }

    FString DsImpactBare(FGameplayEventData EvData)
    {
        if (!EvData.ContextHandle.IsValid())
            return "imp=nocontext";
        FGameplayEffectContext_HitResponse DsHit = EvData.ContextHandle.GetHitContext();
        if (DsHit.Impact == GameplayTag::Combat_HitType_Standard) return "imp=standard";
        if (DsHit.Impact == GameplayTag::Combat_HitType_Additive) return "imp=additive";
        if (DsHit.Impact == GameplayTag::Combat_HitType_Knockback) return "imp=knockback";
        if (DsHit.Impact == GameplayTag::Combat_HitType_Parried) return "imp=parried";
        if (DsHit.Impact == GameplayTag::Combat_HitType_Deflected) return "imp=deflected";
        if (DsHit.Impact == GameplayTag::Combat_HitType_Dot) return "imp=dot";
        return "imp=other";
    }

    // one record into the ring, ring into the window title
    void DsEmit(FString EvType, FString EvPhase, FString EvSrc, FString EvCtx)
    {
        // one ring + one sequence for the whole mod (DsLp_Bus.as); the local ring below is the fallback without the bus
        UDsLp_Bus DsBus = DsLp::Bus();
        if (DsBus != nullptr)
        {
            DsBus.Emit(EvType, EvPhase, EvSrc, EvCtx);
            return;
        }
        m_EventCounter = m_EventCounter + 1;
        int Millis = Math::RoundToInt(Math::Fmod(Gameplay::GetRealTimeSeconds(), 1000000.0) * 1000.0);
        FString DsRec = "" + m_EventCounter + ":" + EvType + ":" + EvPhase + ":t=" + Millis + ",src=" + EvSrc;
        if (EvCtx != "")
            DsRec = DsRec + "," + EvCtx;
        m_Ring.Add(DsRec);
        if (m_Ring.Num() > 8)
            m_Ring.RemoveAt(0);
        FString DsBody = "";
        for (int DsIdx = 0; DsIdx < m_Ring.Num(); DsIdx++)
        {
            if (DsIdx > 0)
                DsBody = DsBody + "|";
            DsBody = DsBody + m_Ring[DsIdx];
        }
        System::SetWindowTitle(FText::FromString("Gothic 1 Remake [G1RDS2|" + DsBody + "]"));
    }
}
