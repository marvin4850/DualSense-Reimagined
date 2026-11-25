// G1R DualSense Bridge - test checklist on screen ("quests" for testing new haptics; test HUD v1, 15.11.2026).
//
// DsBridge decides WHAT is shown (the test plan haptics/tests/active.json, task done = the effect really played) and writes
// Mods/G1R_DualSenseBridge/DsB_TestHud.txt (UTF-8). This file only DRAWS it: a small list in the top right corner of the HUD.
// File format (one item per line):  show=0|1  /  title=<text>  /  task=<0|1>|<display text>
// The SHARE button (read by DsBridge, a test tool) toggles show. No file / show=0 / no tasks -> nothing on screen.
// Hosted as a widget extension (official ModKit) on an always-present HUD widget; the first host with a canvas root wins.

namespace DsBHud
{
    const FString FilePath = "Mods/G1R_DualSenseBridge/DsB_TestHud.txt";
    const float PollSec = 0.25;
    const int FontSize = 17;
}

class UDsB_TestHudState : UScriptGameInstanceSubsystem
{
    UPROPERTY()
    UObject Owner;          // the extension that currently draws the list
}

UCLASS(Abstract)
class UDsB_TestHudBase : UGothicModExt_WidgetExtension
{
    UPROPERTY()
    private UVerticalBox Box;
    UPROPERTY()
    private TArray<UTextBlock> Lines;
    private float SincePoll = 1.0;
    private FString LastRaw = "?";
    private bool bTried = false;

    UFUNCTION(BlueprintOverride)
    bool RequiresTick() const
    {
        return true;
    }

    UFUNCTION(BlueprintOverride)
    void Destruct()
    {
        UDsB_TestHudState St = Cast<UDsB_TestHudState>(Subsystem::GetGameInstanceSubsystem(UDsB_TestHudState::StaticClass()));
        if (St != nullptr && St.Owner == this)
            St.Owner = nullptr;
        if (Box != nullptr)
            Box.RemoveFromParent();
        Box = nullptr;
        Lines.Empty();
        bTried = false;
        LastRaw = "?";
    }

    UFUNCTION(BlueprintOverride)
    void Tick(FGeometry MyGeometry, float InDeltaTime)
    {
        SincePoll += InDeltaTime;
        if (SincePoll < DsBHud::PollSec)
            return;
        SincePoll = 0.0;
        if (!EnsureBox())
            return;
        FString Raw;
        if (!FFileHelper::LoadFileToString(Raw, FPaths::ProjectDir() + DsBHud::FilePath))
            Raw = "";
        if (Raw == LastRaw)
            return;
        LastRaw = Raw;
        Render(Raw);
    }

    private bool EnsureBox()
    {
        if (Box != nullptr)
            return true;
        if (bTried)
            return false;
        bTried = true;
        UDsB_TestHudState St = Cast<UDsB_TestHudState>(Subsystem::GetGameInstanceSubsystem(UDsB_TestHudState::StaticClass()));
        if (St == nullptr || (St.Owner != nullptr && St.Owner != this))
            return false;
        UUserWidget Host = GetWidget();
        if (Host == nullptr)
            return false;
        UCanvasPanel Canvas = Cast<UCanvasPanel>(Host.GetRootWidget());
        if (Canvas == nullptr)
            return false;
        Box = Cast<UVerticalBox>(Host.ConstructWidget(UVerticalBox));
        if (Box == nullptr)
            return false;
        UCanvasPanelSlot BoxSlot = Canvas.AddChildToCanvas(Box);
        if (BoxSlot != nullptr)
        {
            BoxSlot.SetAnchors(FAnchors(1.0, 0.0, 1.0, 0.0));
            BoxSlot.SetAlignment(FVector2D(1.0, 0.0));
            BoxSlot.SetPosition(FVector2D(-36.0, 150.0));
            BoxSlot.SetAutoSize(true);
            BoxSlot.SetZOrder(200);
        }
        Box.SetVisibility(ESlateVisibility::Collapsed);
        St.Owner = this;
        UDsLp_Bus Bus = DsLp::Bus();
        if (Bus != nullptr)
            Bus.Emit("testhud", "update", "hud", "host=" + Host.GetClass().GetName().ToString());
        return true;
    }

    private UTextBlock Line(int Index)
    {
        while (Lines.Num() <= Index)
        {
            UTextBlock Text = Cast<UTextBlock>(GetWidget().ConstructWidget(UTextBlock));
            if (Text == nullptr)
                return nullptr;
            FSlateFontInfo Info = Text.Font;
            UFont Font = Cast<UFont>(DsLpGame::LoadAsset(DsLp::LabelFont));
            if (Font != nullptr)
            {
                Info.FontObject = Font;
                Info.TypefaceFontName = n"Default";
            }
            Info.Size = DsBHud::FontSize;
            Text.SetFont(Info);
            Text.SetShadowOffset(FVector2D(1.5, 1.5));
            Text.SetJustification(ETextJustify::Right);
            Box.AddChildToVerticalBox(Text);
            Lines.Add(Text);
        }
        return Lines[Index];
    }

    private void Render(FString Raw)
    {
        TArray<FString> Rows = String::ParseIntoArray(Raw, "\n", true);
        bool bShow = false;
        int Used = 0;
        for (FString Row : Rows)
        {
            FString R = Row.Replace("\r", "");
            if (R.StartsWith("show="))
            {
                bShow = R == "show=1";
                continue;
            }
            bool bTitle = R.StartsWith("title=");
            bool bTask = R.StartsWith("task=");
            if (!bTitle && !bTask)
                continue;
            UTextBlock T = Line(Used);
            if (T == nullptr)
                break;
            Used++;
            if (bTitle)
            {
                T.SetText(FText::FromString(R.RightChop(6)));
                T.SetColorAndOpacity(FSlateColor(FLinearColor(1.0, 0.84, 0.55, 1.0)));
                continue;
            }
            FString Body = R.RightChop(5);          // <0|1>|text
            bool bDone = Body.StartsWith("1");
            T.SetText(FText::FromString(Body.RightChop(2)));
            if (bDone)
                T.SetColorAndOpacity(FSlateColor(FLinearColor(0.45, 0.95, 0.45, 1.0)));
            else
                T.SetColorAndOpacity(FSlateColor(FLinearColor(0.92, 0.92, 0.88, 0.95)));
        }
        for (int Idx = 0; Idx < Lines.Num(); Idx++)
        {
            if (Idx < Used)
                Lines[Idx].SetVisibility(ESlateVisibility::HitTestInvisible);
            else
                Lines[Idx].SetVisibility(ESlateVisibility::Collapsed);
        }
        if (bShow && Used > 0)
            Box.SetVisibility(ESlateVisibility::HitTestInvisible);
        else
            Box.SetVisibility(ESlateVisibility::Collapsed);
    }
}

// Two candidate hosts: the notification layer and the crosshair widget (both live during gameplay). Only one draws (state Owner).
class UDsB_TestHudOnNotifications : UDsB_TestHudBase
{
    default AddToWidgetOfClass = n"/Game/UI/Notifications/W_NotificationMain.W_NotificationMain_C";
}

class UDsB_TestHudOnCrosshair : UDsB_TestHudBase
{
    default AddToWidgetOfClass = n"/Game/UI/Crosshair/BP_CrosshairWidget.BP_CrosshairWidget_C";
}
