// Touchpad v10 (v9: names layer host; v10: clock reset on load, softer line of sight): official widget extensions only. No outlines, shader changes or save/config edits.
// Left swipe (v6, user 16.11): names of all usable objects around, after the "Loot Highlight" mod (Nexus 643,
// labels.lua) without its outlines: one text block per object with the game's own name (InteractiveComponent
// DisplayName). Font, size, colour and shadow are copied at runtime from the game's live interaction label
// (Player_Widget.W_ObjectInteraction) - the original look.
// v8 (user 17.11): (1) menus - swipe left/right switches the tab of the management menu (inventory, quests, ...);
// (2) map - one finger drags, two fingers pinch-zoom (DsBridge sends the view in DsB_TouchMap.txt, the map widget gets a
// render transform, the game's own map logic is untouched); (3) names only for objects in direct line of sight of the
// hero AND the camera - walls hide them; (4) the names layer is the LOWEST child of Player_Widget, so the game's own HUD
// (interaction label, bars, hints) and every other UI draw above the names; hidden while any menu is open.
// Command (3 fields): <unique key>|<game real ms copied from heartbeat>|<action>.
// Action: world - map / names / journal / quicksave; menu - tab_next / tab_prev.
// Status (4 fields): <game real ms>|<world, menu or map>|<names 0/1>|<last key>.
// Map view (4 integers): <epoch>|<scale*1000>|<tx*1000>|<ty*1000> (translation in units of the map widget size).
namespace DsBTouch
{
    const FString Command = "Mods/G1R_DualSenseBridge/DsB_TouchCommand.txt";
    const FString Status = "Mods/G1R_DualSenseBridge/DsB_TouchStatus.txt";
    const FString MapView = "Mods/G1R_DualSenseBridge/DsB_TouchMap.txt";
    const float RadiusCm = 1000.0;
    const float FallbackFontSize = 14.0;
    const int MaxLabels = 32;
    const float SeenWindow = 0.30; // a menu/map extension that ticked this recently is on screen
    const float SightSlackCm = 70.0; // a hit this close to the target still counts as "seen" (the object's own base / mesh)

    // Scavenger whistle (v48, user 21.11.2026: blow into the pad microphone = use the whistle). The game's own call-mount
    // ability (native base UGameplayAbilityCallMount: whistle montage, mount spawn, cooldown) is looked up among the hero's
    // granted abilities and activated as the game would; without the whistle / on cooldown the game itself refuses.
    FString CallMount()
    {
        AGothicCharacter Hero = DsLpGame::Hero();
        if (Hero == nullptr)
            return "whistle_nohero";
        UAbilitySystemComponent Asc = Hero.GetAbilitySystemComponent();
        if (Asc == nullptr)
            return "whistle_noasc";
        TArray<FGameplayAbilitySpecHandle> Handles;
        Asc.GetAllAbilities(Handles);
        for (FGameplayAbilitySpecHandle H : Handles)
        {
            bool bInstance = false;
            const UGameplayAbility A = AbilitySystem::GetGameplayAbilityFromSpecHandle(Asc, H, bInstance);
            if (A == nullptr || !A.IsA(UGameplayAbilityCallMount))
                continue;
            return Asc.TryActivateAbility(H) ? "whistle_ok" : "whistle_refused";
        }
        return "whistle_none";
    }

    UDsB_TouchState State()
    {
        return Cast<UDsB_TouchState>(Subsystem::GetGameInstanceSubsystem(UDsB_TouchState::StaticClass()));
    }

    bool InWorld()
    {
        UG1RGameInstance GI = Cast<UG1RGameInstance>(Gameplay::GetGameInstance());
        return GI != nullptr && GI.GetInputMode() == EGameInputMode::Game && !Gameplay::IsGamePaused()
            && Gameplay::GetPlayerCharacter(0) != nullptr;
    }

    // Decimal parser avoids locale-dependent conversion and rejects malformed / overflowing numbers.
    int Millis(FString Value)
    {
        if (Value.Len() == 0 || Value.Len() > 10)
            return -1;
        int Result = 0;
        FString Digits = "0123456789";
        for (int Idx = 0; Idx < Value.Len(); Idx++)
        {
            int D = Digits.Find(Value.Mid(Idx, 1));
            if (D < 0 || Result > 214748364 || (Result == 214748364 && D > 7))
                return -1;
            Result = Result * 10 + D;
        }
        return Result;
    }

    // Signed variant for the map view; Ok = false on malformed input.
    int Signed(FString Value, bool& Ok)
    {
        bool bNeg = Value.Len() > 1 && Value.Mid(0, 1) == "-";
        int V = Millis(bNeg ? Value.Mid(1, Value.Len() - 1) : Value);
        Ok = V >= 0 && V <= 100000;
        return bNeg ? -V : V;
    }

    FString Clean(FString V)
    {
        return V.Replace(",", " ").Replace("|", " ").Replace(":", " ").Replace("=", " ");
    }

    // Line of sight (v8, user 17.11): nothing blocks the straight line From -> To except the hero and the object itself.
    // v10 (game 17.11 11:36: hidden=2344 label-frames, almost nothing shown): the trace starts 40 cm out (not inside the
    // head, the held torch/weapon or a camera blocking volume) and a hit within SightSlackCm of the target counts as seen
    // (the table / shelf the thing stands on, the object's own separate meshes).
    bool Clear(FVector From, FVector To, TArray<AActor>& Ignore)
    {
        FVector Dir = To - From;
        float Len = Dir.Size();
        if (Len <= SightSlackCm + 40.0)
            return true;
        FVector Start = From + Dir * (40.0 / Len);
        FHitResult Hit;
        if (!System::LineTraceSingle(Start, To, ETraceTypeQuery::TraceTypeQuery1, false, Ignore, EDrawDebugTrace::None, Hit, true))
            return true;
        return Hit.Location.Distance(To) <= SightSlackCm;
    }

    // Every text block inside a widget (panels, content widgets and nested user widgets), in tree order.
    void CollectTexts(UWidget W, TArray<UTextBlock>& Out)
    {
        if (W == nullptr)
            return;
        UTextBlock T = Cast<UTextBlock>(W);
        if (T != nullptr)
        {
            Out.Add(T);
            return;
        }
        UUserWidget U = Cast<UUserWidget>(W);
        if (U != nullptr)
        {
            CollectTexts(U.GetRootWidget(), Out);
            return;
        }
        UPanelWidget P = Cast<UPanelWidget>(W);
        if (P != nullptr)
        {
            for (int Idx = 0; Idx < P.GetChildrenCount(); Idx++)
                CollectTexts(P.GetChildAt(Idx), Out);
        }
    }

    // The canvas nearest to the root of a widget tree (breadth first, visible branches only, nested user widgets included).
    UCanvasPanel FindCanvas(UWidget Root)
    {
        TArray<UWidget> Queue;
        if (Root != nullptr)
            Queue.Add(Root);
        int Head = 0;
        while (Head < Queue.Num() && Head < 400)
        {
            UWidget W = Queue[Head];
            Head++;
            if (W == nullptr || !W.IsVisible())
                continue;
            UCanvasPanel C = Cast<UCanvasPanel>(W);
            if (C != nullptr)
                return C;
            UUserWidget U = Cast<UUserWidget>(W);
            if (U != nullptr)
            {
                Queue.Add(U.GetRootWidget());
                continue;
            }
            UPanelWidget P = Cast<UPanelWidget>(W);
            if (P != nullptr)
            {
                for (int Idx = 0; Idx < P.GetChildrenCount(); Idx++)
                    Queue.Add(P.GetChildAt(Idx));
            }
        }
        return nullptr;
    }

    // Every visible widget of a tree (hidden / collapsed branches are skipped), in tree order.
    void CollectVisible(UWidget W, TArray<UWidget>& Out, int Depth)
    {
        if (W == nullptr || Depth > 40 || Out.Num() > 4000 || !W.IsVisible())
            return;
        Out.Add(W);
        UUserWidget U = Cast<UUserWidget>(W);
        if (U != nullptr)
        {
            CollectVisible(U.GetRootWidget(), Out, Depth + 1);
            return;
        }
        UPanelWidget P = Cast<UPanelWidget>(W);
        if (P != nullptr)
        {
            for (int Idx = 0; Idx < P.GetChildrenCount(); Idx++)
                CollectVisible(P.GetChildAt(Idx), Out, Depth + 1);
        }
    }

    // Menu swipe: the first visible tab list of the open management menu goes one tab on (wraps around).
    // CommonUI tab list first (the menu's top tabs), the game's own UTabSelectorWidget as fallback.
    FString SwitchTab(UUserWidget Menu, bool bNext)
    {
        if (Menu == nullptr)
            return "no_menu";
        TArray<UWidget> All;
        CollectVisible(Menu, All, 0);
        for (UWidget W : All)
        {
            UCommonTabListWidgetBase Tabs = Cast<UCommonTabListWidgetBase>(W);
            if (Tabs == nullptr || Tabs.GetTabCount() < 2)
                continue;
            int Count = Tabs.GetTabCount();
            FName Current = Tabs.GetSelectedTabId();
            int At = 0;
            for (int Idx = 0; Idx < Count; Idx++)
            {
                if (Tabs.GetTabIdAtIndex(Idx) == Current)
                    At = Idx;
            }
            // Disabled / hidden tabs refuse the selection: try the following ones.
            for (int Step = 1; Step < Count; Step++)
            {
                int Next = (At + (bNext ? Step : Count - Step)) % Count;
                FName Id = Tabs.GetTabIdAtIndex(Next);
                if (Tabs.SelectTabByID(Id, false))
                    return "tablist_" + Clean(Id.ToString());
            }
            return "tablist_refused";
        }
        for (UWidget W : All)
        {
            UTabSelectorWidget Sel = Cast<UTabSelectorWidget>(W);
            if (Sel == nullptr)
                continue;
            int Before = Sel.GetTabIndex();
            if (bNext)
                Sel.NextTab();
            else
                Sel.PreviousTab();
            return "selector_" + Before + "_" + Sel.GetTabIndex();
        }
        return "no_tabs";
    }
}

class UDsB_TouchState : UScriptGameInstanceSubsystem
{
    UPROPERTY()
    bool NamesEnabled = false;
    UPROPERTY()
    UObject LabelOwner;
    // Style of the game's own interaction label, copied once from the live Player_Widget.W_ObjectInteraction.
    bool bStyleReady = false;
    int StyleTries = 0;
    FSlateFontInfo NativeFont;
    FSlateColor NativeColor;
    FVector2D NativeShadowOffset;
    FLinearColor NativeShadowColor;

    // Shared command pump: whichever touchpad extension ticks (HUD, management menu, map) runs it, at most every 30 ms.
    UPROPERTY()
    UPlayerWidget PlayerUI;
    float PlayerSeenAt = -10.0;
    UPROPERTY()
    UUserWidget MenuWidget;
    float MenuSeenAt = -10.0;
    UPROPERTY()
    UUserWidget MapWidget;
    float MapSeenAt = -10.0;
    float LastPump = -10.0;
    bool bBaseline = false;
    FString LastKey = "none";
    FString LastRaw = "";
    float LastSave = -10.0;
    UPROPERTY()
    UClass JournalClass;
    // Map view: the file content when this map opened (older views are ignored) and the widget we transformed.
    bool bMapOpen = false;
    FString MapBaseline = "";
    FString MapLastRaw = "";
    UPROPERTY()
    UWidget MapTarget;
    int MapApplied = 0;
    float HudFailedAt = -1.0; // the HUD host has no canvas for the names layer: fallback hosts may take it

    bool MenuOnScreen()
    {
        float Now = Gameplay::GetRealTimeSeconds();
        return (MenuWidget != nullptr && Now >= MenuSeenAt && Now - MenuSeenAt < DsBTouch::SeenWindow)
            || (MapWidget != nullptr && Now >= MapSeenAt && Now - MapSeenAt < DsBTouch::SeenWindow)
            || (PlayerUI != nullptr && PlayerUI.AreAnyMenusOpen());
    }

    void Pump()
    {
        float Now = Gameplay::GetRealTimeSeconds();
        if (Now < LastPump)
        {
            // v10 (game 17.11 11:36): the world's real time restarts on a load / level change. Old timestamps then lie
            // in the future and froze the pump (and the touchpad) until the new clock caught up - minutes.
            DsLp::Bus().Emit("touch", "update", "widget", "clock_reset=1");
            LastPump = -10.0;
            LastSave = -10.0;
            PlayerSeenAt = Now;
            MenuSeenAt = -10.0;
            MapSeenAt = -10.0;
        }
        if (Now - LastPump < 0.03)
            return;
        LastPump = Now;
        int NowMs = Math::RoundToInt(Now * 1000.0);
        bool bMap = MapWidget != nullptr && Now >= MapSeenAt && Now - MapSeenAt < DsBTouch::SeenWindow;
        bool bMenu = MenuWidget != nullptr && Now >= MenuSeenAt && Now - MenuSeenAt < DsBTouch::SeenWindow;
        bool bWorld = !bMap && !bMenu && PlayerUI != nullptr && Now >= PlayerSeenAt && Now - PlayerSeenAt < DsBTouch::SeenWindow
            && DsBTouch::InWorld() && !PlayerUI.AreAnyMenusOpen();
        FString Mode = bMap ? "map" : (bWorld ? "world" : "menu");

        FString Raw;
        FFileHelper::LoadFileToString(Raw, FPaths::ProjectDir() + DsBTouch::Command);
        if (!bBaseline)
        {
            // Old commands never fire when a widget is created or the game loads.
            LastRaw = Raw;
            bBaseline = true;
            DsLp::Bus().Emit("touch", "start", "widget", "ver=11");
        }
        else if (Raw != LastRaw)
        {
            LastRaw = Raw;
            TArray<FString> Fields = String::ParseIntoArray(Raw, "|", false);
            if (Fields.Num() == 3 && Fields[0] != LastKey)
            {
                LastKey = Fields[0]; // consume once, including blocked commands; never retry a quicksave
                int SentMs = DsBTouch::Millis(Fields[1]);
                FString Action = Fields[2];
                bool bFresh = SentMs >= 0 && NowMs >= SentMs && NowMs - SentMs <= 1000;
                FString Result = "blocked";
                if (bFresh && bWorld)
                    Result = WorldAction(Action, Now);
                else if (bFresh && bMenu && !bMap && (Action == "tab_next" || Action == "tab_prev"))
                    Result = DsBTouch::SwitchTab(MenuWidget, Action == "tab_next");
                DsLp::Bus().Emit("touch", "update", "gesture", "act=" + Action + ",mode=" + Mode + ",result=" + Result);
            }
        }
        UpdateMap(bMap);
        FString StatusLine = "" + NowMs + "|" + Mode + "|" + (NamesEnabled ? "1" : "0") + "|" + LastKey;
        FFileHelper::SaveStringToFile(StatusLine, FPaths::ProjectDir() + DsBTouch::Status, FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
    }

    FString WorldAction(FString Action, float Now)
    {
        if (Action == "names")
        {
            NamesEnabled = !NamesEnabled;
            return NamesEnabled ? "names_on" : "names_off";
        }
        if (Action == "map")
        {
            PlayerUI.OpenMapMenu();
            return "map_requested";
        }
        if (Action == "whistle")
            return DsBTouch::CallMount();
        if (Action == "quicksave" && Now - LastSave >= 2.0)
        {
            LastSave = Now;
            PlayerUI.QuickSave();
            return "save_requested"; // game may reject saving (combat, dialogue, etc.)
        }
        if (Action == "journal")
        {
            if (JournalClass == nullptr)
                JournalClass = Cast<UClass>(DsLpGame::LoadAsset("/Game/UI/ManagementUI/Quests/W_Quests_Main.W_Quests_Main_C"));
            UManagementContentWidgetBase Defaults = JournalClass != nullptr
                ? Cast<UManagementContentWidgetBase>(JournalClass.DefaultObject) : nullptr;
            if (Defaults == nullptr)
                return "journal_missing";
            // Read the game's actual quest tab instead of guessing a gameplay tag.
            PlayerUI.OpenManagementPage(Defaults.m_TabTag, FGameplayTag());
            return "journal_requested";
        }
        return "blocked";
    }

    // The visible map of the map screen: world map, else the area map.
    UWidget MapImage()
    {
        UMapMain Main = Cast<UMapMain>(MapWidget);
        if (Main == nullptr)
            return nullptr;
        if (Main.Map_World != nullptr && Main.Map_World.IsVisible())
            return Main.Map_World;
        if (Main.Map_Area != nullptr && Main.Map_Area.IsVisible())
            return Main.Map_Area;
        return nullptr;
    }

    // v48 diagnostics (user 21.11: "whatever map item / type is chosen, the standard world map opens"): which map config each
    // map widget of the map screen shows (UUIMapConfigWorld / OldMine / Cliff / OldCamp ...), empty flag, area map visibility.
    FString MapInfo()
    {
        UMapMain Main = Cast<UMapMain>(MapWidget);
        if (Main == nullptr)
            return ",info=none";
        FString R = "";
        if (Main.Map_World != nullptr)
            R += ",wvis=" + (Main.Map_World.IsVisible() ? "1" : "0") + ",wdata=" + (Main.Map_World.m_ActiveMapData != nullptr
                ? DsBTouch::Clean(Main.Map_World.m_ActiveMapData.GetClass().GetName().ToString()) : "none") + ",wempty=" + (Main.Map_World.m_IsEmpty ? "1" : "0");
        if (Main.Map_Area != nullptr)
            R += ",avis=" + (Main.Map_Area.IsVisible() ? "1" : "0") + ",adata=" + (Main.Map_Area.m_ActiveMapData != nullptr
                ? DsBTouch::Clean(Main.Map_Area.m_ActiveMapData.GetClass().GetName().ToString()) : "none") + ",aempty=" + (Main.Map_Area.m_IsEmpty ? "1" : "0");
        return R;
    }

    void ResetTarget()
    {
        if (MapTarget != nullptr)
        {
            MapTarget.SetRenderScale(FVector2D(1.0, 1.0));
            MapTarget.SetRenderTranslation(FVector2D(0.0, 0.0));
        }
        MapTarget = nullptr;
    }

    void UpdateMap(bool bMap)
    {
        FString Raw;
        if (bMap)
            FFileHelper::LoadFileToString(Raw, FPaths::ProjectDir() + DsBTouch::MapView);
        if (bMap != bMapOpen)
        {
            // A new map screen starts from the game's own view; views written before it are ignored.
            if (!bMap)
                DsLp::Bus().Emit("touch", "update", "map", "open=0,applied=" + MapApplied);
            bMapOpen = bMap;
            ResetTarget();
            MapBaseline = Raw;
            MapLastRaw = Raw;
            MapApplied = 0;
            return;
        }
        if (!bMap)
            return;
        UWidget Target = MapImage();
        if (Target != MapTarget)
        {
            ResetTarget(); // world <-> area switch: the new map starts unzoomed too
            MapTarget = Target;
            MapBaseline = Raw;
            MapLastRaw = Raw;
            if (Target != nullptr)
            {
                Target.SetRenderTransformPivot(FVector2D(0.5, 0.5));
                UPanelWidget Parent = Target.GetParent();
                if (Parent != nullptr)
                    Parent.SetClipping(EWidgetClipping::ClipToBounds); // a zoomed map stays inside its frame
                DsLp::Bus().Emit("touch", "update", "map", "open=1,target=" + DsBTouch::Clean(Target.GetName().ToString())
                    + ",parent=" + (Parent != nullptr ? DsBTouch::Clean(Parent.GetName().ToString()) : "none") + MapInfo());
            }
            return;
        }
        if (Target == nullptr || Raw == MapLastRaw || Raw == MapBaseline)
            return;
        MapLastRaw = Raw;
        TArray<FString> F = String::ParseIntoArray(Raw.TrimStartAndEnd(), "|", false);
        if (F.Num() != 4)
            return;
        bool Ok1 = false;
        bool Ok2 = false;
        bool Ok3 = false;
        int S = DsBTouch::Signed(F[1], Ok1);
        int X = DsBTouch::Signed(F[2], Ok2);
        int Y = DsBTouch::Signed(F[3], Ok3);
        if (!Ok1 || !Ok2 || !Ok3 || S < 1000 || S > 4000)
            return;
        float Scale = S / 1000.0;
        FVector2D Size = Slate::GetLocalSize(Target.GetCachedGeometry());
        Target.SetRenderScale(FVector2D(Scale, Scale));
        Target.SetRenderTranslation(FVector2D(X / 1000.0 * Size.X, Y / 1000.0 * Size.Y));
        MapApplied++;
    }
}

// Player_Widget is the game's UPlayerWidget; use its own commands and normal save restrictions.
class UDsB_TouchCommands : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    private float SinceStyle = 1.0;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    // The name text of the game's interaction label: a text block whose name says "Name", else the first one.
    private void CaptureStyle(UDsB_TouchState St, UPlayerWidget PlayerUI)
    {
        St.StyleTries++;
        if (St.StyleTries > 100000)
            St.StyleTries = 61;
        TArray<UTextBlock> Texts;
        DsBTouch::CollectTexts(PlayerUI.W_ObjectInteraction, Texts);
        UTextBlock Pick = nullptr;
        FString Seen = "";
        for (UTextBlock T : Texts)
        {
            FString N = T.GetName().ToString();
            Seen += (Seen == "" ? "" : ";") + N;
            if (Pick == nullptr && N.Contains("Name"))
                Pick = T;
        }
        if (Pick == nullptr && Texts.Num() > 0)
            Pick = Texts[0];
        if (Pick != nullptr)
        {
            St.NativeFont = Pick.Font;
            St.NativeColor = Pick.ColorAndOpacity;
            St.NativeShadowOffset = Pick.ShadowOffset;
            St.NativeShadowColor = Pick.ShadowColorAndOpacity;
            St.bStyleReady = true;
        }
        if (Pick != nullptr || St.StyleTries == 60)
            DsLp::Bus().Emit("touch", "update", "names", "style=" + (Pick != nullptr ? "native" : "fallback")
                + ",text=" + (Pick != nullptr ? Pick.GetName().ToString() : "none")
                + ",font=" + (Pick != nullptr && Pick.Font.FontObject != nullptr ? Pick.Font.FontObject.GetName().ToString() : "none")
                + ",size=" + (Pick != nullptr ? Math::RoundToInt(Pick.Font.Size) : 0) + ",texts=" + Seen);
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        UDsB_TouchState St = DsBTouch::State();
        UPlayerWidget PlayerUI = Cast<UPlayerWidget>(GetWidget());
        if (St == nullptr || PlayerUI == nullptr)
            return;
        St.PlayerUI = PlayerUI;
        St.PlayerSeenAt = Gameplay::GetRealTimeSeconds();
        St.Pump();
        SinceStyle += Delta;
        if (!St.bStyleReady && SinceStyle >= 0.10)
        {
            SinceStyle = 0.0;
            CaptureStyle(St, PlayerUI);
        }
    }
}

// The management menu (inventory, quests, glossary, stats): ticks only while it is on screen.
class UDsB_TouchOnMenu : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/ManagementUI/W_ManagementUI_Main.W_ManagementUI_Main_C";

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        UDsB_TouchState St = DsBTouch::State();
        if (St == nullptr || GetWidget() == nullptr)
            return;
        St.MenuWidget = GetWidget();
        St.MenuSeenAt = Gameplay::GetRealTimeSeconds();
        St.Pump();
    }
}

// The map screen (UMapMain: world map and area map): ticks only while it is on screen.
class UDsB_TouchOnMap : UGothicModExt_WidgetExtension
{
    default AddToWidgetOfClass = n"/Game/UI/ManagementUI/Map/W_Map_Main.W_Map_Main_C";

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        UDsB_TouchState St = DsBTouch::State();
        if (St == nullptr || GetWidget() == nullptr)
            return;
        St.MapWidget = GetWidget();
        St.MapSeenAt = Gameplay::GetRealTimeSeconds();
        St.Pump();
    }
}

// Names layer. v8 (user 17.11): lowest child (ZOrder -1000) of a canvas in Player_Widget, so the game's own HUD draws
// above it. v8.1 (game 17.11 11:24: v8 never made a layer - the Player_Widget root is not a canvas): the first visible
// canvas inside Player_Widget is used; if there is none, the v6-v7 hosts (crosshair, notifications) take over, also at -1000.
UCLASS(Abstract)
class UDsB_NearbyNames : UGothicModExt_WidgetExtension
{
    UPROPERTY()
    bool bFallback = true;
    UPROPERTY()
    private UCanvasPanel Layer;
    UPROPERTY()
    private TArray<AInteractiveObjectActor> Objects;
    UPROPERTY()
    private TArray<UTextBlock> Labels;
    private float SinceScan = 1.0;
    private bool bWasShowing = false;
    private bool bNativeStyled = false;
    private int Hidden = 0; // label-frames hidden by walls during the last switch-on (diagnostics)
    private int ShownFrames = 0; // label-frames drawn during the last switch-on

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const { return true; }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        UDsB_TouchState St = DsBTouch::State();
        if (St != nullptr && St.LabelOwner == this)
            St.LabelOwner = nullptr;
        if (Layer != nullptr)
            Layer.RemoveFromParent();
        Layer = nullptr;
        Objects.Empty();
        Labels.Empty();
    }

    private bool EnsureLayer()
    {
        if (Layer != nullptr)
            return true;
        UDsB_TouchState St = DsBTouch::State();
        if (St == nullptr || (St.LabelOwner != nullptr && St.LabelOwner != this))
            return false;
        UUserWidget Host = GetWidget();
        if (Host == nullptr)
            return false;
        // v8.1: fallback hosts (crosshair, notifications) only after the HUD host found no canvas.
        if (bFallback && St.HudFailedAt < 0.0)
            return false;
        UCanvasPanel Root = DsBTouch::FindCanvas(Host.GetRootWidget());
        if (Root == nullptr)
        {
            if (!bFallback && St.HudFailedAt < 0.0)
            {
                St.HudFailedAt = Gameplay::GetRealTimeSeconds();
                UWidget R = Host.GetRootWidget();
                DsLp::Bus().Emit("touch", "update", "names", "layer=none,host=" + DsBTouch::Clean(Host.GetName().ToString())
                    + ",root=" + (R != nullptr ? DsBTouch::Clean(R.GetClass().GetName().ToString()) : "null"));
            }
            return false;
        }
        Layer = Cast<UCanvasPanel>(Host.ConstructWidget(UCanvasPanel));
        if (Layer == nullptr)
            return false;
        // Lowest layer of the HUD: a canvas draws its children by ZOrder, every game child is above -1000.
        int Siblings = Root.GetChildrenCount();
        UCanvasPanelSlot Slot = Root.AddChildToCanvas(Layer);
        Slot.SetAnchors(FAnchors(0.0, 0.0, 1.0, 1.0));
        Slot.SetOffsets(FMargin(0.0));
        Slot.SetZOrder(-1000);
        Layer.SetVisibility(ESlateVisibility::HitTestInvisible);
        St.LabelOwner = this;
        DsLp::Bus().Emit("touch", "update", "names", "layer=" + DsBTouch::Clean(Host.GetName().ToString()) + ",canvas="
            + DsBTouch::Clean(Root.GetName().ToString()) + ",z=-1000,siblings=" + Siblings);
        return true;
    }

    private bool Eligible(AInteractiveObjectActor Object, AGothicCharacter Hero)
    {
        if (Object == nullptr || Object.IsActorBeingDestroyed() || Object.IsHidden())
            return false;
        if (Object.ActorLocation.Distance(Hero.ActorLocation) > DsBTouch::RadiusCm)
            return false;
        UInteractiveComponent IC = Object.GetInteractiveComponent();
        if (IC == nullptr || !IC.IsUsable() || IC.GetForceDisableInteraction() || IC.GetForceDisableUI() || IC.IsBeingUsed()
            || IC.IsSelectedToInteract())
            return false;
        // v7 (user 16.11): only what the HERO can really use. The game's own flags: NPC-only objects (lamps
        // "light the fire" of daily routines) have m_UsableByPlayer off.
        // Seats (benches, chairs, stools: Action.Interact.Sit and below) are too many and pointless - skipped.
        UInteractiveObjectDefinition Def = IC.m_InteractItem;
        if (Def == nullptr || !Def.m_UsableByPlayer)
            return false;
        if (Def.GetDefaultInteraction().MatchesTag(GameplayTag::Action_Interact_Sit))
            return false;
        AGothicCharacterState HeroState = Hero.GetCharacterState();
        return HeroState != nullptr && IC.CanAnyInteractionSpotBeClaimed(HeroState);
    }

    private void Scan(AGothicCharacter Hero)
    {
        // Query the game's spatial interaction index, only while enabled. Keep the nearest unique objects.
        Objects.Empty();
        TArray<AInteractiveObjectActor> Found;
        GetAllActorsOfClass(AInteractiveObjectActor, Found);
        for (AInteractiveObjectActor Object : Found)
        {
            if (Objects.Contains(Object) || !Eligible(Object, Hero))
                continue;
            float Distance = Object.ActorLocation.Distance(Hero.ActorLocation);
            int At = 0;
            while (At < Objects.Num() && Objects[At].ActorLocation.Distance(Hero.ActorLocation) <= Distance)
                At++;
            if (At >= DsBTouch::MaxLabels)
                continue;
            Objects.Insert(Object, At);
            if (Objects.Num() > DsBTouch::MaxLabels)
                Objects.RemoveAt(Objects.Num() - 1);
        }
        if (!bWasShowing)
        {
            // Diagnostics once per switch-on: what is labelled and by which default interaction (tuning the filters).
            FString Shown = "";
            for (int Idx = 0; Idx < Objects.Num() && Idx < 10; Idx++)
            {
                UInteractiveObjectDefinition Def = Objects[Idx].GetInteractiveComponent().m_InteractItem;
                Shown += (Idx > 0 ? ";" : "") + Objects[Idx].GetInteractiveComponent().DisplayName().ToString() + "/"
                    + (Def != nullptr ? Def.GetDefaultInteraction().ToString() : "none");
            }
            DsLp::Bus().Emit("touch", "update", "names", "n=" + Objects.Num() + ",hidden=" + Hidden + ",drawn=" + ShownFrames + ",shown=" + DsBTouch::Clean(Shown));
            Hidden = 0;
            ShownFrames = 0;
        }
    }

    // v8: the label point must be seen straight from the hero's eyes AND from the camera; walls, rocks and closed doors hide it.
    private bool InSight(AInteractiveObjectActor Object, AGothicCharacter Hero, APlayerController PC, FVector Point)
    {
        TArray<AActor> Ignore;
        Ignore.Add(Hero);
        Ignore.Add(Object);
        // Two target points: the label point and the object's own centre; either one seen is enough.
        FVector Body = Object.ActorLocation + FVector(0.0, 0.0, 25.0);
        FVector Eyes = Hero.ActorLocation + FVector(0.0, 0.0, Hero.BaseEyeHeight);
        if (!DsBTouch::Clear(Eyes, Point, Ignore) && !DsBTouch::Clear(Eyes, Body, Ignore))
            return false;
        APlayerCameraManager Cam = PC.PlayerCameraManager;
        if (Cam == nullptr)
            return true;
        FVector CamAt = Cam.GetCameraLocation();
        return DsBTouch::Clear(CamAt, Point, Ignore) || DsBTouch::Clear(CamAt, Body, Ignore);
    }

    private UTextBlock Label(int Index)
    {
        while (Labels.Num() <= Index)
        {
            UTextBlock Text = Cast<UTextBlock>(GetWidget().ConstructWidget(UTextBlock));
            if (Text == nullptr)
                return nullptr;
            UDsB_TouchState St = DsBTouch::State();
            if (St != nullptr && St.bStyleReady)
            {
                Text.SetFont(St.NativeFont);
                Text.SetColorAndOpacity(St.NativeColor);
                Text.SetShadowOffset(St.NativeShadowOffset);
                Text.SetShadowColorAndOpacity(St.NativeShadowColor);
            }
            else
            {
                FSlateFontInfo Info = Text.Font;
                UFont Font = Cast<UFont>(DsLpGame::LoadAsset(DsLp::LabelFont));
                if (Font != nullptr)
                {
                    Info.FontObject = Font;
                    Info.TypefaceFontName = n"Default";
                }
                Info.Size = DsBTouch::FallbackFontSize;
                Text.SetFont(Info);
                Text.SetColorAndOpacity(FSlateColor(FLinearColor(0.95, 0.93, 0.85, 1.0)));
                Text.SetShadowOffset(FVector2D(1.0, 1.0));
            }
            Text.SetJustification(ETextJustify::Center);
            UCanvasPanelSlot Slot = Layer.AddChildToCanvas(Text);
            Slot.SetAutoSize(true);
            Slot.SetAlignment(FVector2D(0.5, 1.0));
            Labels.Add(Text);
        }
        return Labels[Index];
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float Delta)
    {
        UDsB_TouchState St = DsBTouch::State();
        AGothicCharacter Hero = DsLpGame::Hero();
        APlayerController PC = Gameplay::GetPlayerController(0);
        // v8: any open menu, map or dialogue screen hides the names (UI always wins).
        bool bShow = St != nullptr && St.NamesEnabled && DsBTouch::InWorld() && Hero != nullptr && PC != nullptr
            && !St.MenuOnScreen();
        if (!bShow)
        {
            if (Layer != nullptr)
                Layer.SetVisibility(ESlateVisibility::Collapsed);
            if (bWasShowing)
                Objects.Empty();
            bWasShowing = false;
            return; // switch remains ON through menus; no timed auto-off
        }
        if (!EnsureLayer())
            return;
        Layer.SetVisibility(ESlateVisibility::HitTestInvisible);
        if (!bNativeStyled && St.bStyleReady)
        {
            bNativeStyled = true;
            for (UTextBlock L : Labels)
            {
                L.SetFont(St.NativeFont);
                L.SetColorAndOpacity(St.NativeColor);
                L.SetShadowOffset(St.NativeShadowOffset);
                L.SetShadowColorAndOpacity(St.NativeShadowColor);
            }
        }
        SinceScan += Delta;
        if (!bWasShowing || SinceScan >= 1.0)
        {
            Scan(Hero);
            SinceScan = 0.0;
        }
        bWasShowing = true;
        int Used = 0;
        TArray<FVector2D> Placed;
        for (AInteractiveObjectActor Object : Objects)
        {
            if (!Eligible(Object, Hero) || !Object.WasRecentlyRendered(0.3))
                continue;
            FVector Position = Object.ActorLocation + FVector(0.0, 0.0, 25.0);
            if (Object.UINamePosition != nullptr)
                Position = Object.UINamePosition.WorldLocation + FVector(0.0, 0.0, 12.0);
            if (!InSight(Object, Hero, PC, Position))
            {
                Hidden++;
                continue;
            }
            FVector2D Screen;
            if (!WidgetLayout::ProjectWorldLocationToWidgetPosition(PC, Position, Screen, false))
                continue;
            FVector2D Absolute = Slate::LocalToAbsolute(WidgetLayout::GetViewportWidgetGeometry(), Screen);
            FVector2D Local = Slate::AbsoluteToLocal(Layer.GetCachedGeometry(), Absolute);
            FVector2D Size = Slate::GetLocalSize(Layer.GetCachedGeometry());
            if (Local.X < 30.0 || Local.Y < 24.0 || Local.X > Size.X - 30.0 || Local.Y > Size.Y - 12.0)
                continue;
            bool bClashes = false;
            for (FVector2D Other : Placed)
            {
                if (Math::Abs(Local.X - Other.X) < 140.0 && Math::Abs(Local.Y - Other.Y) < 20.0)
                    bClashes = true;
            }
            if (bClashes)
                continue;
            FText ShownName = Object.GetInteractiveComponent().DisplayName();
            if (ShownName.ToString() == "" || ShownName.ToString().Contains("MISSING STRING TABLE"))
                continue;
            UTextBlock Text = Label(Used);
            if (Text == nullptr)
                break;
            Text.SetText(ShownName);
            Cast<UCanvasPanelSlot>(Text.Slot).SetPosition(Local);
            Text.SetVisibility(ESlateVisibility::HitTestInvisible);
            Placed.Add(Local);
            Used++;
            ShownFrames++;
        }
        for (int Idx = Used; Idx < Labels.Num(); Idx++)
            Labels[Idx].SetVisibility(ESlateVisibility::Collapsed);
    }
}

// Primary host: the HUD (v8).
class UDsB_NearbyNamesOnHud : UDsB_NearbyNames
{
    default AddToWidgetOfClass = n"/Game/UI/Player/Player_Widget.Player_Widget_C";
    default bFallback = false;
}

// Fallback hosts (v6-v7), used only when the HUD has no canvas.
class UDsB_NearbyNamesOnCrosshair : UDsB_NearbyNames
{
    default AddToWidgetOfClass = n"/Game/UI/Crosshair/BP_CrosshairWidget.BP_CrosshairWidget_C";
}

class UDsB_NearbyNamesOnNotifications : UDsB_NearbyNames
{
    default AddToWidgetOfClass = n"/Game/UI/Notifications/W_NotificationMain.W_NotificationMain_C";
}
