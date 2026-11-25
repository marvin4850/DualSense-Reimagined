// G1R DualSense Bridge — the event bus (window title ring) shared by every part of the mod, plus what the
// Tactile Lockpicking part keeps for the whole run of the game (lock database, its log).
//
// One game instance subsystem = one sequence counter for all records: the gameplay listener (DualSenseBridge.as) and the
// lockpicking window extension (DsLp_Lockpick.as) write into the same ring, so DsBridge sees one gap-free stream.
// Transport v2: "Gothic 1 Remake [G1RDS2|<rec>|...]", <rec> = <seq>:<type>:<phase>:t=<ms>,src=<hook>[,key=value...]
// (values only [A-Za-z0-9_]); ring of the last 8 records, newest last.

class UDsLp_Bus : UScriptGameInstanceSubsystem
{
    UPROPERTY()
    int m_Seq = 0;
    UPROPERTY()
    TArray<FString> m_Ring;

    // Tactile Lockpicking: lock graphs (built once, on the first minigame) and the diagnostic log.
    UPROPERTY()
    UDsLp_LockDb m_Db;
    UPROPERTY()
    UDsLp_Log m_Log;
    // TEMPORARY test switch (DsLp_Lockpick.as): -1 = tactile profile follows the hero's skill, 0..2 = chosen by hand
    UPROPERTY()
    int m_LpSkillOverride = -1;

    UFUNCTION(BlueprintOverride)
    void Initialize()
    {
        m_Log = Cast<UDsLp_Log>(NewObject(this, UDsLp_Log::StaticClass()));
        m_Log.bDebug = true;
        m_Log.Open("G1R_DualSense_Lockpick.log");
        m_Log.Line("G1R DualSense Bridge: Tactile Lockpicking log (Saved folder of the game)");
    }

    UDsLp_LockDb Db()
    {
        if (m_Db == nullptr)
        {
            m_Db = Cast<UDsLp_LockDb>(NewObject(this, UDsLp_LockDb::StaticClass()));
            DsLp_RegisterLockGraphs(m_Db);
            if (m_Log != nullptr)
                m_Log.Line(f"lock database: {m_Db.Num()} locks ({DsLp::LocksBuild})");
        }
        return m_Db;
    }

    void Emit(FString EvType, FString EvPhase, FString EvSrc, FString EvCtx)
    {
        m_Seq = m_Seq + 1;
        int Millis = Math::RoundToInt(Math::Fmod(Gameplay::GetRealTimeSeconds(), 1000000.0) * 1000.0);
        FString DsRec = "" + m_Seq + ":" + EvType + ":" + EvPhase + ":t=" + Millis + ",src=" + EvSrc;
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

namespace DsLp
{
    UDsLp_Bus Bus()
    {
        return Cast<UDsLp_Bus>(Subsystem::GetGameInstanceSubsystem(UDsLp_Bus::StaticClass()));
    }
}
