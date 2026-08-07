using System.Text.Json;
using Craft.Configuration;
using Craft.Storage;

namespace Craft.Setup;

/// <summary>
/// First-user seeding and allowedUsers table probes for the setup wizard.
/// </summary>
public class SetupUserBootstrap
{
    private readonly ILogger<SetupUserBootstrap> _logger;
    private readonly CraftSettings _settings;
    private readonly IUserTableStore _store;

    public SetupUserBootstrap(ILogger<SetupUserBootstrap> logger, CraftSettings settings, IUserTableStore store)
    {
        _logger = logger;
        _settings = settings;
        _store = store;
    }

    /// <summary>
    /// Resolves the user table name with the same sanitization as AuthService.
    /// </summary>
    private string ResolveUserTableName()
    {
        var raw = _settings.Auth.UserTableName;
        var sanitized = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        if (sanitized.Length > 63) sanitized = sanitized[..63];
        if (sanitized.Length < 3) sanitized = "allowedUsers";
        return sanitized;
    }

    /// <summary>
    /// Checks the allowedUsers table status: whether it's reachable and whether
    /// it already contains any users.
    /// </summary>
    public async Task<SetupService.AllowedUsersStatus> CheckAllowedUsersStatus(CancellationToken ct = default)
    {
        try
        {
            var tableName = ResolveUserTableName();
            await _store.EnsureTableAsync(tableName, ct);

            var count = 0;
            await foreach (var row in _store.QueryTableAsync(tableName, ct))
            {
                if (!row.RowKey.StartsWith('_'))
                {
                    count++;
                    if (count > 0) break; // We only need to know if any exist
                }
            }

            return new SetupService.AllowedUsersStatus
            {
                Connected = true,
                HasUsers = count > 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Setup] Failed to check allowedUsers table");
            return new SetupService.AllowedUsersStatus
            {
                Connected = false,
                HasUsers = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Seeds the first user into the allowedUsers table with the roles from
    /// Setup.FirstUserRoles (defaults to "superadmin" when unset).
    /// Only works when the table is empty — refuses if users already exist.
    /// Uses the same entity schema as CIPP-API's Invoke-ExecCIPPUsers.
    /// </summary>
    public async Task SeedFirstUser(string upn, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
            throw new ArgumentException("UPN (email) is required.");

        // Invariant, not current-culture: this value is an identity key compared against rows
        // written by AuthService (which already lowercases invariantly). Under a Turkish locale
        // the two would disagree on "I"/"i" and a seeded user would fail to match.
        upn = upn.Trim().ToLowerInvariant();

        var tableName = ResolveUserTableName();
        await _store.EnsureTableAsync(tableName, ct);

        // Guard: refuse if the table already has users
        await foreach (var row in _store.QueryTableAsync(tableName, ct))
        {
            if (!row.RowKey.StartsWith('_'))
                throw new InvalidOperationException("The allowed users table already contains users. First-user seeding is only available on an empty table.");
        }

        string[] roles = _settings.Setup.FirstUserRoles.Count > 0
            ? _settings.Setup.FirstUserRoles.ToArray()
            : ["superadmin"];
        var rolesJson = JsonSerializer.Serialize(roles);

        var userRow = new StoreRow("User", upn)
        {
            Properties =
            {
                ["Roles"] = rolesJson,
                ["ManualRoles"] = rolesJson,
                ["AutoRoles"] = "[]",
                ["Source"] = "Manual"
            }
        };

        await _store.UpsertAsync(tableName, userRow, ct);
        _logger.LogInformation("[Setup] Seeded first user {Upn} with roles {Roles}", upn, string.Join(",", roles));
    }
}
