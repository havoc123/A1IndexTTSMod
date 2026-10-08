namespace LocalModManager.Abstractions
{
    public enum FeaturePluginState { Stopped, Starting, Running, Stopping, Failed }
    public interface IManagedFeaturePlugin
    {
        string FeatureId { get; }
        string DisplayName { get; }
        string Description { get; }
        string FeatureVersion { get; }
        bool DesiredEnabled { get; }
        FeaturePluginState State { get; }
        string StatusMessage { get; }
        void SetEnabled(bool enabled);
    }
}
