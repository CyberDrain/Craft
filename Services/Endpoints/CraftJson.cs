using System.Text.Json;

namespace Craft.Endpoints;

/// <summary>
/// The JSON conventions native endpoints read and write by default.
///
/// <para>
/// <see cref="JsonSerializerDefaults.Web"/>, which means camelCase property names and case-insensitive
/// reads — the same options ASP.NET Core's own minimal APIs and MVC use. Using
/// <c>System.Text.Json</c>'s bare defaults instead would make <c>{"hostname":"x"}</c> fail to bind to a
/// <c>Hostname</c> property and would emit <c>{"Hostname":…}</c> back, so every application would
/// discover the same trap and paste the same options object into every endpoint.
/// </para>
/// </summary>
internal static class CraftJson
{
    /// <summary>Shared instance — <see cref="JsonSerializerOptions"/> caches its metadata, so reusing one is what keeps serialization fast.</summary>
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
