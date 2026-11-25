// Magic observer (mod v44, 16.11.2026). Read-only: nothing in casting, damage or animation is changed.
// The hero's cast / channel montages (A_Man_*_Cast*, A_Man_*_Launch*, A_Man_UnTransform) are reported with the item in hand
// (rune or scroll class, e.g. ItAr_Scroll_TransformWolf - the only way to tell the 17 transformations apart, they share one
// montage) and with every section change, so DsBridge times the spell's HD components to the montage's own notifies:
//   spell:start    m=<montage>,itm=<item class>,pos=0
//   spell:update   m,itm,sec=<new section>,pos=<montage ms>     (section NAME change only, not a loop wrap)
//   spell:complete m,int=0|1                                      (int=1: interrupted, e.g. hit or not enough mana)
// Damage dealt while the hero is in magic stance gets ",mag=1,itm=<item>" on attack:update res=contact (projectile hits).
// DsBridge rules: haptics/packs/89-magic.json (tools/magic_library.py).
namespace DsMagic
{
    bool IsMagicMontage(FString N)
    {
        if (!N.StartsWith("A_Man_") || N.Contains("ScrollRune")) return false;
        return N.Contains("_Cast") || N.Contains("_Launch") || N.Contains("UnTransform");
    }

    FString ItemName()
    {
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr || Hero.GetCarryComponent() == nullptr) return "none";
        UItemDefinition Item = Hero.GetCarryComponent().GetEquippedItemDefinition();
        if (Item == nullptr) return "none";
        return Item.GetClass().GetName().ToString();
    }

    FString DamageCtx()
    {
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr || !Hero.HasGameplayTag(GameplayTag::State_Combat_Magic)) return "";
        return ",mag=1,itm=" + ItemName();
    }
}
