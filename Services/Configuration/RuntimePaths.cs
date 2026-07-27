namespace Craft.Configuration;

/// <summary>
/// File-backed logging with size-based rotation.
/// Logs are written to {Directory}/{FilePrefix}.log and rotated to
/// {FilePrefix}.1.log, {FilePrefix}.2.log, etc. when MaxFileSizeMB is exceeded.
/// Oldest files beyond MaxFileCount are automatically deleted.
/// </summary>
/// <summary>
/// Default writable base directory for app-owned runtime state (logs, restart
/// tracker) when no explicit path is configured. Resolves the current user's home
/// — $HOME, or the passwd entry, which is /home/app for the distroless image's
/// non-root APP_UID — and falls back to /home/app if none is reported. Per-setting
/// config (App__FileLogging__Directory, App__ContainerHealth__TrackerDirectory)
/// still overrides it.
/// </summary>
internal static class RuntimePaths
{
    internal static string Home
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? "/home/app" : home;
        }
    }
}
