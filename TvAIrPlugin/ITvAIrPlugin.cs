namespace TvAIrPlugin;

public interface ITvAIrPlugin
{
    string Name { get; }
    string Version { get; }
    void Initialize(IPluginContext context);
    void OnStart();
    void OnStop();
    void OnRecordingStarted(PluginRecordingInfo info) { }
    void OnRecordingStopped(PluginRecordingInfo info) { }
    void OnPlaybackStarted(PluginPlaybackInfo info) { }
    void OnPlaybackStopped(PluginPlaybackInfo info) { }
    void OnPlaybackPositionChanged(PluginPlaybackPosition position) { }
}
