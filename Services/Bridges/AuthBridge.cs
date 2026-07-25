using Craft.Auth;

// NAMESPACE PINNED — do not change.
// Downstream PowerShell reaches these types by fully-qualified name, e.g.
//   [Craft.Services.RealtimeBridge]::Publish($userId, $jobId, 'start', $data)
// Renaming the namespace compiles fine and then fails at runtime in the hosted app
// ("Unable to find type"). Type forwarding cannot help — it only works across assemblies.
// The folder is free to move; the namespace is a published contract.
namespace Craft.Services;

/// <summary>
/// Static bridge so PowerShell can trigger an auth-config reload without DI.
/// Call [Craft.Services.AuthBridge]::ReloadAuth() from PS after credentials change.
/// </summary>
public static class AuthBridge
{
    private static AuthService? s_service;
    public static void Initialize(AuthService service) => s_service = service;

    /// <summary>
    /// Reloads auth configuration (clears the allowedUsers cache) after credentials change.
    /// Safe to call from PowerShell: [Craft.Services.AuthBridge]::ReloadAuth()
    /// </summary>
    public static void ReloadAuth() => s_service?.ReloadConfiguration();

    /// <summary>
    /// Invalidates the allowedUsers cache so changes take effect immediately.
    /// Safe to call from PowerShell: [Craft.Services.AuthBridge]::InvalidateUsers()
    /// </summary>
    public static void InvalidateUsers() => s_service?.InvalidateUserCache();
}
