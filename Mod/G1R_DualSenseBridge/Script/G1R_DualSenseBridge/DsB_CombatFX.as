// G1R DualSense Bridge - combat wave 3 (16.11.2026): melee impacts from the game's own impact FX definitions.
// Source: Sound/FXDefinitions/FXDefinitions_AttackImpact.as. Every melee contact runs PrepareFX of one of these script classes
// (weapon -> body, weapon -> weapon = a block, weapon deflected by the world). The class replacement (ReplaceParentClass, the same
// mechanism that works for UDrawWeapon_Human since game95) runs the game's code first (Super): sound parameters, particles and
// gameplay are unchanged; then, only if the hero is the attacker or the target, one event is sent:
//   impactfx:start kind=body|weapon|deflect, side=out (hero hits) | in (hero is hit / blocks), wpn=<attacker weapon family>,
//                  def=<defender weapon family>, mat=wood|metal|fists (weapon contact), armor=<IMP_targetArmor>, crt=<target creature>,
//                  blk=<hero blocking tag>, tmat/amat=<target/attacker material>, tsize/asize=<small|mid|big>
// DsBridge rules: packs/86-combat-wave3.json.
namespace DsCombat
{
    FString Family(AGothicCharacter C)
    {
        if (C == nullptr) return "none";
        if (C.HasGameplayTag(GameplayTag::Species_Creature)) return "creature";
        if (C.GetCarryComponent() == nullptr) return "fist";
        UItemDefinition Item = C.GetCarryComponent().GetEquippedItemDefinition();
        if (Item == nullptr) return "fist";
        FGameplayTag Type = Item.GetItemType();
        if (Type == GameplayTag::Item_Weapon_Mace_OneHand) return "m1h";
        if (Type == GameplayTag::Item_Weapon_Mace_TwoHand) return "m2h";
        if (Type == GameplayTag::Item_Weapon_Axe_OneHand) return "a1h";
        if (Type == GameplayTag::Item_Weapon_Axe_TwoHand) return "a2h";
        if (Type == GameplayTag::Item_Weapon_Sword_OneHand) return "s1h";
        if (Type == GameplayTag::Item_Weapon_Sword_TwoHand) return "s2h";
        if (Type == GameplayTag::Item_Weapon_Staff_TwoHand) return "staff";
        if (Type == GameplayTag::Item_Weapon_Torch_OneHand) return "torch";
        if (Type == GameplayTag::Item_Weapon_Fist) return "fist";
        return "other";
    }

    // What the blow lands on (user, 16.11.2026: a scavenger and a stone golem must feel different):
    // stone | ice | bone | zombie | chitin | creature | metal | leather | flesh
    FString Material(AGothicCharacter C)
    {
        if (C == nullptr) return "none";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Golem_Ice)) return "ice";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Golem)) return "stone";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Undead_Zombie)) return "zombie";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Undead)) return "bone";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Minecrawler)) return "chitin";
        if (C.HasGameplayTag(GameplayTag::Species_Creature)) return "creature";
        if (C.HasGameplayTag(GameplayTag::ArmorMaterial_MineCrawler)) return "chitin";
        if (C.HasGameplayTag(GameplayTag::ArmorMaterial_Metal) || C.HasGameplayTag(GameplayTag::ArmorMaterial_Ore)) return "metal";
        if (C.HasGameplayTag(GameplayTag::ArmorMaterial_Leather)) return "leather";
        return "flesh";
    }

    // Body mass: small | mid | big (big creatures get an extra low "weight" layer)
    FString Size(AGothicCharacter C)
    {
        if (C == nullptr) return "mid";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Troll) || C.HasGameplayTag(GameplayTag::Species_Creature_Shadowbeast)
            || C.HasGameplayTag(GameplayTag::Species_Creature_Swampshark) || C.HasGameplayTag(GameplayTag::Species_Creature_Golem)
            || C.HasGameplayTag(GameplayTag::Species_Creature_Demon) || C.HasGameplayTag(GameplayTag::Species_Creature_Lurker)
            || C.HasGameplayTag(GameplayTag::Species_Creature_Minecrawler_Queen) || C.HasGameplayTag(GameplayTag::Species_Creature_Sleeper)
            || C.HasGameplayTag(GameplayTag::Species_Creature_Molerat_Queen) || C.HasGameplayTag(GameplayTag::Species_Creature_Scavenger_Adult))
            return "big";
        if (C.HasGameplayTag(GameplayTag::Species_Creature_Meatbug) || C.HasGameplayTag(GameplayTag::Species_Creature_Rat)
            || C.HasGameplayTag(GameplayTag::Species_Creature_Bloodfly) || C.HasGameplayTag(GameplayTag::Species_Creature_Minecrawler_Nymph)
            || C.HasGameplayTag(GameplayTag::Species_Creature_Golem_Ice_Mini))
            return "small";
        return "mid";
    }

    // Material of a weapon clash, decided like the game's own impact FX: no weapon visual on either side = fists (or a
    // creature), wood if either weapon is wood, otherwise metal.
    FString ClashMat(AGothicCharacter A, AGothicCharacter B)
    {
        if (A == nullptr || B == nullptr || A.GetCarryComponent() == nullptr || B.GetCarryComponent() == nullptr
            || A.GetCarryComponent().GetItemVisual() == nullptr || B.GetCarryComponent().GetItemVisual() == nullptr)
            return "fists";
        if (A.GetCarryComponent().GetItemVisual().WeaponMaterialTag.MatchesTag(GameplayTag::WeaponMaterial_Wood)
            || B.GetCarryComponent().GetItemVisual().WeaponMaterialTag.MatchesTag(GameplayTag::WeaponMaterial_Wood))
            return "wood";
        return "metal";
    }

    int Param(UGothicFXDefinition Def, FName Name)
    {
        if (Def == nullptr || !Def.FModEvent.EventParameters.Contains(Name)) return -1;
        return Math::RoundToInt(Def.FModEvent.EventParameters[Name]);
    }

    void Report(UGothicFXDefinition Def, FString Kind, AActor Instigator, const FHitResult &inout Hit)
    {
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr || Def == nullptr) return;
        AGothicCharacter Attacker = Cast<AGothicCharacter>(Instigator);
        AGothicCharacter Target = nullptr;
        if (Hit.GetComponent() != nullptr) Target = Cast<AGothicCharacter>(Hit.GetComponent().GetOwner());
        FString Side = "";
        if (Attacker == Hero) Side = "out";
        else if (Target == Hero) Side = "in";
        if (Side == "") return;
        FString Mat = "metal";
        if (Kind == "weapon")
        {
            if (Attacker == nullptr || Target == nullptr || Attacker.GetCarryComponent() == nullptr || Target.GetCarryComponent() == nullptr
                || Attacker.GetCarryComponent().GetItemVisual() == nullptr || Target.GetCarryComponent().GetItemVisual() == nullptr)
                Mat = "fists";
            else if (Attacker.GetCarryComponent().GetItemVisual().WeaponMaterialTag.MatchesTag(GameplayTag::WeaponMaterial_Wood)
                || Target.GetCarryComponent().GetItemVisual().WeaponMaterialTag.MatchesTag(GameplayTag::WeaponMaterial_Wood))
                Mat = "wood";
        }
        int Crt = Target != nullptr && Target.HasGameplayTag(GameplayTag::Species_Creature) ? 1 : 0;
        int Blk = Hero.HasGameplayTag(GameplayTag::State_Block) ? 1 : 0;
        UDsLp_Bus Bus = DsLp::Bus();
        if (Bus != nullptr)
            Bus.Emit("impactfx", "start", "fx_hit", "kind=" + Kind + ",side=" + Side + ",wpn=" + Family(Attacker) + ",def=" + Family(Target)
                + ",mat=" + Mat + ",armor=" + Param(Def, n"IMP_targetArmor") + ",crt=" + Crt
                + ",blk=" + Blk + ",tmat=" + Material(Target) + ",tsize=" + Size(Target)
                + ",amat=" + Material(Attacker) + ",asize=" + Size(Attacker));
    }
}

UCLASS(meta=(ReplaceParentClass))
class UGothicFXDefinition_Impact_Hit_Character_DsCombat : UGothicFXDefinition_Impact_Hit_Character
{
    UFUNCTION()
    void PrepareFX_Implementation(AActor Instigator, const FHitResult &inout HitResult)
    {
        Super::PrepareFX_Implementation(Instigator, HitResult);
        DsCombat::Report(this, "body", Instigator, HitResult);
    }
}

UCLASS(meta=(ReplaceParentClass))
class UGothicFXDefinition_Impact_Hit_Weapon_DsCombat : UGothicFXDefinition_Impact_Hit_Weapon
{
    UFUNCTION()
    void PrepareFX_Implementation(AActor Instigator, const FHitResult &inout HitResult)
    {
        Super::PrepareFX_Implementation(Instigator, HitResult);
        DsCombat::Report(this, "weapon", Instigator, HitResult);
    }
}

UCLASS(meta=(ReplaceParentClass))
class UGothicFXDefinition_Impact_Hit_Environment_Deflected_DsCombat : UGothicFXDefinition_Impact_Hit_Environment_Deflected
{
    UFUNCTION()
    void PrepareFX_Implementation(AActor Instigator, const FHitResult &inout HitResult)
    {
        Super::PrepareFX_Implementation(Instigator, HitResult);
        DsCombat::Report(this, "deflect", Instigator, HitResult);
    }
}
