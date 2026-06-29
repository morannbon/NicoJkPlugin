namespace TvAIrPlugin;

public interface IPluginContext
{
    string AppDirectory { get; }
    string DataDirectory { get; }
    string PluginDataDirectory { get; }

    IReadOnlyList<PluginEpgEvent> GetEpg(PluginEpgQuery? query = null);
    IReadOnlyList<PluginReservation> GetReservations(PluginReservationQuery? query = null);
    IReadOnlyList<PluginReservation> GetReservationHistory(PluginReservationHistoryQuery? query = null);
    IReadOnlyList<PluginTunerStatus> GetTunerStatus();
    IReadOnlyList<PluginConflictInfo> GetConflicts();
    PluginReservationPreview PreviewReservationAllocation(PluginReservationDraft draft);
    IReadOnlyList<PluginChainInfo> GetChainCandidates(PluginChainQuery? query = null);
    PluginChainPreview PreviewChainReservation(PluginReservationDraft draft);
    PluginReservationOperationResult AddReservation(PluginReservationDraft draft);
    PluginReservationOperationResult UpdateReservation(PluginReservationUpdate update);
    PluginReservationOperationResult DeleteReservation(int reservationId, bool force = false);

    string? ReadPluginFile(string relativePath);
    void WritePluginFile(string relativePath, string content);
    string? ReadPluginSettings(string name = "settings.json");
    void WritePluginSettings(string content, string name = "settings.json");

    void Log(string message);
    void Log(PluginLogLevel level, string message);
    void NotifyInfo(string message);
    void NotifyWarning(string message);
    void NotifyError(string message);
    void AddTimelineEvent(string title, string message);
    void AddAuditLog(string action, string message);
}
