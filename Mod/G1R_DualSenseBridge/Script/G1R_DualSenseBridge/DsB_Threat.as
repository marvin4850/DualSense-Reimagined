// Threat observer v1. Official ModKit only; no AI, relationships or stealth mechanics are changed.
// Reads the OBSERVER's perception of the hero, never the hero's perception of the NPC.
// State.Combat is merely a drawn weapon; AIState.Conflict.Combat is the actual NPC combat state.
namespace DsThreat
{
    // Copies of GAS/GASCharacterMixins IsInConflictFightingState/IsInConflictWarningState (decompiled 1.1a):
    // the game's global functions don't resolve from a mod module ("No matching signatures"), so tags are read directly.
    bool IsFighting(AGothicCharacter C)
    {
        if (!C.HasGameplayTag(GameplayTag::AIState_Conflict)) return false;
        return C.HasGameplayTag(GameplayTag::AIState_Conflict_Combat)
            || C.HasGameplayTag(GameplayTag::AIState_Conflict_TurnToAttacker)
            || C.HasGameplayTag(GameplayTag::AIState_Conflict_Paralyzed)
            || C.HasGameplayTag(GameplayTag::AIState_Conflict_ReactToTakingDamage)
            || C.HasGameplayTag(GameplayTag::AIState_Conflict_Flee_GetGuardHelp);
    }

    bool IsWarning(AGothicCharacter C)
    {
        return C.HasGameplayTag(GameplayTag::AIState_Conflict_Warning)
            && !C.HasGameplayTag(GameplayTag::AIState_Conflict_Warning_BlockedPath);
    }

    bool IsAsleep(AGothicCharacter C)
    {
        return C.HasGameplayTag(GameplayTag::Debuff_Sleep) || C.HasGameplayTag(GameplayTag::Action_Ambient_Sleep)
            || C.HasGameplayTag(GameplayTag::Action_Bed) || C.HasGameplayTag(GameplayTag::AIState_MagicSleep);
    }

    bool IsPerson(AGothicCharacter C)
    {
        AGothicCharacterState S = C.GetCharacterState();
        return S != nullptr && (S.GetSpecies().MatchesTag(GameplayTag::Species_Human) || S.GetSpecies().MatchesTag(GameplayTag::Species_Orc));
    }

    void Poll(UGA_DualSenseBridge_Listener Listener, AGothicCharacter Hero, bool InWorld)
    {
        float Now = Gameplay::GetRealTimeSeconds();
        bool Sneaking = Hero.HasGameplayTag(GameplayTag::State_Movement_Crouching);
        int Seen = 0;
        int Fighters = 0;
        if (InWorld && !Hero.HasGameplayTag(GameplayTag::State_Dead) && !Hero.HasGameplayTag(GameplayTag::State_Defeated))
        {
            // Discovery only once a second; current state/perception every 100 ms. Streamed NPCs are included.
            if (Now >= Listener.m_ThreatRefreshAt)
            {
                Listener.m_ThreatActors.Empty();
                GetAllActorsOfClass(AGothicCharacter, Listener.m_ThreatActors);
                Listener.m_ThreatRefreshAt = Now + 1.0f;
            }
            for (AGothicCharacter Other : Listener.m_ThreatActors)
            {
                if (!IsValid(Other) || Other == Hero || Other.HasGameplayTag(GameplayTag::State_Dead) || Other.HasGameplayTag(GameplayTag::State_Defeated)) continue;
                UGameplayAbility_CharacterAI_Gothic AI = Cast<UGameplayAbility_CharacterAI_Gothic>(Other.GetCharacterAI());
                if (AI == nullptr) continue;
                AGothicCharacter Target = AI.GetTargetEnemy();
                bool Directed = Target == Hero || (Target == nullptr && AI.GetCharacterOfInterest() == Hero);
                bool Fighting = IsFighting(Other);
                if (Directed && Fighting)
                {
                    Fighters++;
                    continue;
                }
                // A fight directed at someone else must not trigger either colour.
                if (Fighting) continue;
                // v2 (user 16.11): only useful information. Sleepers (same tags the game's own perception ignores,
                // CharacterAI_Human) and harmless creatures (meatbugs) never light yellow.
                if (IsAsleep(Other)) continue;
                if (Other.GetPerception() == nullptr || !Other.GetPerception().CanSense(Hero, 0.0f)) continue;
                bool Warning = Directed && IsWarning(Other);
                // Investigating / searching / watching the hero (any non-fight conflict state aimed at him).
                bool Reacting = Directed && IsPerson(Other) && Other.HasGameplayTag(GameplayTag::AIState_Conflict);
                bool Hostile = Other.IsHostileTowards(Hero);
                // Sneaking: an awake human/orc who actually SEES the hero (theft/lockpick witness), not merely hears.
                bool Witness = Sneaking && IsPerson(Other)
                    && Other.GetCharacterState() != nullptr && Hero.GetCharacterState() != nullptr
                    && Other.GetCharacterState().CanSeeCharacter(Hero.GetCharacterState());
                if (Hostile || Warning || Reacting || Witness) Seen++;
            }
        }
        FString Level = Fighters > 0 ? "combat" : (Seen > 0 ? "noticed" : "idle");
        FString State = "level=" + Level + ",sneak=" + (Sneaking ? 1 : 0) + ",seen=" + Seen + ",fighters=" + Fighters + ",v=2";
        // Heartbeat leases the light, restores it after menu/reconnect and prevents a stuck warning if telemetry stops.
        if (State != Listener.m_ThreatLast || Now - Listener.m_ThreatSentAt >= 0.5f)
        {
            Listener.DsEmit("threat", "update", "ai_perception", State);
            Listener.m_ThreatLast = State;
            Listener.m_ThreatSentAt = Now;
        }
    }
}
