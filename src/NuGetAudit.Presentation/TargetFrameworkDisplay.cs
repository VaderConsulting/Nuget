using System.Globalization;
using NuGet.Frameworks;

namespace NuGetAudit.Presentation;

/// <summary>
/// Maps target framework monikers (short folder names or <c>.NETFramework,Version=v…</c> style) to readable labels such as <c>.NET Framework 4.5.2</c>.
/// </summary>
public static class TargetFrameworkDisplay
{
    /// <summary>
    /// Formats a package row moniker that may combine several TFMs with <c> / </c> (merged .NET versions).
    /// </summary>
    public static string FormatMergedRowMonikers(string? monikerOrMerged)
    {
        if (monikerOrMerged is null || string.IsNullOrWhiteSpace(monikerOrMerged))
        {
            return "-";
        }

        string Trimmed = monikerOrMerged.Trim();
        if (string.Equals(Trimmed, "-", StringComparison.Ordinal))
        {
            return "-";
        }

        if (Trimmed.IndexOf(" / ", StringComparison.Ordinal) < 0)
        {
            return Format(Trimmed);
        }

        string[] RawParts = Trimmed.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries);
        List<string> Formatted = new();
        foreach (string Raw in RawParts)
        {
            string Part = Raw.Trim();
            if (Part.Length > 0)
            {
                Formatted.Add(Format(Part));
            }
        }

        return Formatted.Count == 0 ? "-" : string.Join("; ", Formatted);
    }

    /// <summary>
    /// Formats a single moniker for UI. Returns the original text when parsing does not yield a specific framework.
    /// </summary>
    public static string Format(string? moniker)
    {
        if (moniker is null || string.IsNullOrWhiteSpace(moniker))
        {
            return "-";
        }

        string Trimmed = moniker.Trim();
        if (string.Equals(Trimmed, "-", StringComparison.Ordinal))
        {
            return "-";
        }

        NuGetFramework? Parsed = TryParse(Trimmed);
        if (Parsed is null || !Parsed.IsSpecificFramework)
        {
            return Trimmed;
        }

        return FormatParsed(Parsed);
    }

    /// <summary>
    /// Formats a project's <c>TargetFrameworks</c> string (semicolon-separated monikers).
    /// </summary>
    public static string FormatProjectMonikers(string? semicolonSeparated)
    {
        if (string.IsNullOrWhiteSpace(semicolonSeparated))
        {
            return "-";
        }

        string Source = semicolonSeparated!;
        string[] RawParts = Source.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> Formatted = new();
        foreach (string Raw in RawParts)
        {
            string Part = Raw.Trim();
            if (Part.Length == 0)
            {
                continue;
            }

            Formatted.Add(Format(Part));
        }

        return Formatted.Count == 0 ? "-" : string.Join("; ", Formatted);
    }

    private static NuGetFramework? TryParse(string text)
    {
        try
        {
            NuGetFramework Candidate = NuGetFramework.Parse(text);
            if (!ReferenceEquals(Candidate, NuGetFramework.UnsupportedFramework) && Candidate.IsSpecificFramework)
            {
                return Candidate;
            }
        }
        catch (ArgumentException)
        {
        }

        try
        {
            NuGetFramework Candidate = NuGetFramework.ParseFolder(text);
            if (!ReferenceEquals(Candidate, NuGetFramework.UnsupportedFramework) && Candidate.IsSpecificFramework)
            {
                return Candidate;
            }
        }
        catch (ArgumentException)
        {
        }

        return null;
    }

    private static string FormatParsed(NuGetFramework framework)
    {
        string FrameworkId = framework.Framework;
        Version Version = framework.Version;
        string VersionText = FormatClrVersion(Version);

        if (string.Equals(FrameworkId, FrameworkConstants.FrameworkIdentifiers.Net, StringComparison.OrdinalIgnoreCase))
        {
            return ".NET Framework " + VersionText;
        }

        if (string.Equals(FrameworkId, FrameworkConstants.FrameworkIdentifiers.NetStandard, StringComparison.OrdinalIgnoreCase))
        {
            return ".NET Standard " + VersionText;
        }

        if (string.Equals(FrameworkId, FrameworkConstants.FrameworkIdentifiers.NetCoreApp, StringComparison.OrdinalIgnoreCase))
        {
            string Base = Version.Major >= 5 ? ".NET " + VersionText : ".NET Core " + VersionText;
            string PlatformSuffix = FormatPlatformSuffix(framework);
            return Base + PlatformSuffix;
        }

        if (string.Equals(FrameworkId, FrameworkConstants.FrameworkIdentifiers.Native, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(VersionText) ? "native" : "native " + VersionText;
        }

        return FrameworkId + " " + VersionText;
    }

    private static string FormatPlatformSuffix(NuGetFramework framework)
    {
        if (string.IsNullOrEmpty(framework.Platform))
        {
            return string.Empty;
        }

        string Platform = framework.Platform.Trim();
        string? PlatformVersionText = FormatPlatformVersionFragment(framework);
        return " (" + Platform + (string.IsNullOrEmpty(PlatformVersionText) ? string.Empty : " " + PlatformVersionText) + ")";
    }

    private static string? FormatPlatformVersionFragment(NuGetFramework framework)
    {
        object? Pv = framework.PlatformVersion;
        if (Pv is null)
        {
            return null;
        }

        if (Pv is string S)
        {
            string T = S.Trim();
            return T.Length == 0 ? null : T;
        }

        if (Pv is Version V)
        {
            return FormatClrVersion(V);
        }

        string? AsText = Convert.ToString(Pv, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(AsText) ? null : AsText;
    }

    private static string FormatClrVersion(Version version)
    {
        if (version.Revision > 0)
        {
            return version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
        }

        if (version.Build >= 0)
        {
            return version.Major + "." + version.Minor + "." + version.Build;
        }

        if (version.Minor > 0)
        {
            return version.Major + "." + version.Minor;
        }

        return version.Major.ToString(CultureInfo.InvariantCulture);
    }
}
