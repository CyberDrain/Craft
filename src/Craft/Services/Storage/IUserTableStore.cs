namespace Craft.Storage;

/// <summary>
/// Table store for the allowedUsers authorization table. Resolves via
/// <see cref="Configuration.AuthSettings.UserStorageConnection"/> when set; otherwise shares the
/// host's <see cref="ICraftTableStore"/> connection (<c>AzureWebJobsStorage</c> /
/// <c>App:Storage:ConnectionString</c>). Orchestrator and other host tables never use the auth override.
/// </summary>
public interface IUserTableStore : ICraftTableStore;
