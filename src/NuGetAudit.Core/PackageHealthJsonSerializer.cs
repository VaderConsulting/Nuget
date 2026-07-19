using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetAudit.Core;

/// <summary>
/// Deserializes <see cref="PackageHealthInfo"/> from persisted JSON with case-insensitive property names and tolerant severity values.
/// </summary>
internal static class PackageHealthJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static PackageHealthInfo DeserializeHealth(string Json)
    {
        if (string.IsNullOrWhiteSpace(Json))
        {
            return PackageHealthInfo.None;
        }

        try
        {
            PackageHealthInfo? Result = JsonSerializer.Deserialize<PackageHealthInfo>(Json, Options);
            return Result ?? PackageHealthInfo.None;
        }
        catch (JsonException)
        {
            return PackageHealthInfo.None;
        }
    }
}
