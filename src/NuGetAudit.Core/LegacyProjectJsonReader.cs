using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NuGetAudit.Core;

/// <summary>
/// Reads legacy <c>project.json</c> files used by early .NET Core / project.json restore so packages and framework keys are visible when the <c>.csproj</c> omits them.
/// </summary>
internal static class LegacyProjectJsonReader
{
    /// <summary>
    /// Reads <c>frameworks</c> keys from <c>project.json</c> for early TFM inference before package references are collected.
    /// </summary>
    /// <param name="ProjectJsonPath">Path to <c>project.json</c>.</param>
    /// <param name="semicolonSeparatedFrameworks">When this method returns <see langword="true"/>, the joined framework keys.</param>
    public static bool TryInferFrameworkList(string ProjectJsonPath, [NotNullWhen(true)] out string? SemicolonSeparatedFrameworks)
    {
        SemicolonSeparatedFrameworks = null;
        if (!File.Exists(ProjectJsonPath))
        {
            return false;
        }

        try
        {
            using JsonDocument Document = JsonDocument.Parse(File.ReadAllText(ProjectJsonPath));
            JsonElement Root = Document.RootElement;
            if (Root.ValueKind != JsonValueKind.Object
                || !Root.TryGetProperty("frameworks", out JsonElement FrameworksJson)
                || FrameworksJson.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            List<string> Keys = new();
            foreach (JsonProperty FrameworkProperty in FrameworksJson.EnumerateObject())
            {
                string EntryKey = FrameworkProperty.Name.Trim();
                if (!string.IsNullOrWhiteSpace(EntryKey))
                {
                    Keys.Add(EntryKey);
                }
            }

            if (Keys.Count == 0)
            {
                return false;
            }

            SemicolonSeparatedFrameworks = string.Join(';', Keys.Distinct(StringComparer.OrdinalIgnoreCase));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="ProjectJsonPath"/> exists and appears to be a dotnet CLI project manifest.
    /// </summary>
    public static bool IsLikelyProjectJsonManifest(string ProjectJsonPath)
    {
        if (!File.Exists(ProjectJsonPath))
        {
            return false;
        }

        try
        {
            string Text = File.ReadAllText(ProjectJsonPath);
            if (string.IsNullOrWhiteSpace(Text))
            {
                return false;
            }

            using JsonDocument Document = JsonDocument.Parse(Text);
            JsonElement Root = Document.RootElement;
            if (Root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return Root.TryGetProperty("dependencies", out _)
                || Root.TryGetProperty("frameworks", out _)
                || Root.TryGetProperty("tools", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to read package ids and optional versions plus framework monikers from <c>project.json</c>.
    /// </summary>
    public static bool TryReadPackages(
        string ProjectJsonPath,
        string ProjectPath,
        string SolutionPath,
        string? ExistingTargetFrameworks,
        out List<PackageReferenceRecord> Packages,
        out string? InferredTargetFrameworks)
    {
        Packages = new List<PackageReferenceRecord>();
        InferredTargetFrameworks = null;

        if (!File.Exists(ProjectJsonPath))
        {
            return false;
        }

        try
        {
            using JsonDocument Document = JsonDocument.Parse(File.ReadAllText(ProjectJsonPath));
            JsonElement Root = Document.RootElement;
            if (Root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            Dictionary<string, string?> TopLevel = ReadDependencyObject(Root, "dependencies");
            List<string> FrameworkKeys = new();

            if (Root.TryGetProperty("frameworks", out JsonElement FrameworksJson) && FrameworksJson.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty FrameworkProperty in FrameworksJson.EnumerateObject())
                {
                    string EntryKey = FrameworkProperty.Name.Trim();
                    if (!string.IsNullOrWhiteSpace(EntryKey))
                    {
                        FrameworkKeys.Add(EntryKey);
                    }

                    if (FrameworkProperty.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (KeyValuePair<string, string?> DependencyPair in ReadDependencyObject(FrameworkProperty.Value, "dependencies"))
                        {
                            if (!TopLevel.ContainsKey(DependencyPair.Key))
                            {
                                TopLevel[DependencyPair.Key] = DependencyPair.Value;
                            }
                        }
                    }
                }
            }

            if (FrameworkKeys.Count > 0 && string.IsNullOrWhiteSpace(ExistingTargetFrameworks))
            {
                InferredTargetFrameworks = string.Join(';', FrameworkKeys.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            string[] TfmList = SplitTargetFrameworks(string.IsNullOrWhiteSpace(ExistingTargetFrameworks) ? InferredTargetFrameworks : ExistingTargetFrameworks);

            foreach (KeyValuePair<string, string?> DependencyPair in TopLevel)
            {
                string PackageId = DependencyPair.Key.Trim();
                if (string.IsNullOrWhiteSpace(PackageId))
                {
                    continue;
                }

                string? Version = DependencyPair.Value?.Trim();
                if (TfmList.Length == 0)
                {
                    Packages.Add(new PackageReferenceRecord(PackageId, Version, null, PackageReferenceKind.Direct, false, null, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(SolutionPath, ProjectPath, null, null, PackageId)));
                }
                else
                {
                    foreach (string Tfm in TfmList)
                    {
                        Packages.Add(new PackageReferenceRecord(PackageId, Version, null, PackageReferenceKind.Direct, false, Tfm, Array.Empty<string>(), Array.Empty<string>(), PackageHealthInfo.None, StableKey.Create(SolutionPath, ProjectPath, Tfm, null, PackageId)));
                    }
                }
            }

            return Packages.Count > 0 || FrameworkKeys.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Dictionary<string, string?> ReadDependencyObject(JsonElement Parent, string PropertyName)
    {
        Dictionary<string, string?> Result = new(StringComparer.OrdinalIgnoreCase);
        if (!Parent.TryGetProperty(PropertyName, out JsonElement Dependencies) || Dependencies.ValueKind != JsonValueKind.Object)
        {
            return Result;
        }

        foreach (JsonProperty Property in Dependencies.EnumerateObject())
        {
            string PackageId = Property.Name.Trim();
            if (string.IsNullOrWhiteSpace(PackageId))
            {
                continue;
            }

            string? Version = null;
            if (Property.Value.ValueKind == JsonValueKind.String)
            {
                Version = Property.Value.GetString();
            }
            else if (Property.Value.ValueKind == JsonValueKind.Object
                && Property.Value.TryGetProperty("version", out JsonElement VersionElement)
                && VersionElement.ValueKind == JsonValueKind.String)
            {
                Version = VersionElement.GetString();
            }

            Result[PackageId] = Version;
        }

        return Result;
    }

    private static string[] SplitTargetFrameworks(string? RawTargetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(RawTargetFrameworks))
        {
            return Array.Empty<string>();
        }

        return RawTargetFrameworks
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
