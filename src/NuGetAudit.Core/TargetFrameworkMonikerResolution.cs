using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Build.Evaluation;
using NuGet.Frameworks;

namespace NuGetAudit.Core;

/// <summary>
/// Resolves NuGet short folder monikers (e.g. <c>netcoreapp2.0</c>) from SDK-style elements, classic TPM strings,
/// and MSBuild <c>TargetFrameworkIdentifier</c> / <c>TargetFrameworkVersion</c> pairs used by older .NET Core projects.
/// </summary>
internal static class TargetFrameworkMonikerResolution
{
    /// <summary>
    /// Reads the best available target framework declaration from unevaluated project XML.
    /// </summary>
    public static string? ReadBestMonikerFromProjectDocument(XDocument Document)
    {
        string? FromSingle = FirstNonEmptyLiteralText(Document, "TargetFramework");
        if (!string.IsNullOrWhiteSpace(FromSingle))
        {
            return FromSingle.Trim();
        }

        string? FromMulti = FirstNonEmptyLiteralText(Document, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(FromMulti))
        {
            return FromMulti.Trim();
        }

        string? FromTpm = FirstNonEmptyLiteralText(Document, "TargetFrameworkMoniker");
        string? Normalized = TryNormalizeTpmOrMonikerToShortName(FromTpm);
        if (!string.IsNullOrWhiteSpace(Normalized))
        {
            return Normalized;
        }

        string? Identifier = FirstNonEmptyLiteralText(Document, "TargetFrameworkIdentifier");
        string? VersionText = FirstNonEmptyLiteralText(Document, "TargetFrameworkVersion");
        string? Profile = FirstNonEmptyLiteralText(Document, "TargetFrameworkProfile");
        return TryBuildShortNameFromMsBuildTriplet(Identifier, VersionText, Profile);
    }

    /// <summary>
    /// Reads evaluated MSBuild properties in the same precedence as <see cref="ReadBestMonikerFromProjectDocument"/>.
    /// </summary>
    public static string? ReadBestMonikerFromEvaluatedProject(Project EvaluatedProject)
    {
        string? Raw = FirstNonEmpty(
            EvaluatedProject.GetPropertyValue("TargetFramework"),
            EvaluatedProject.GetPropertyValue("TargetFrameworks"));

        if (!string.IsNullOrWhiteSpace(Raw))
        {
            return Raw.Trim();
        }

        string? Tpm = FirstNonEmpty(EvaluatedProject.GetPropertyValue("TargetFrameworkMoniker"));
        string? FromTpm = TryNormalizeTpmOrMonikerToShortName(Tpm);
        if (!string.IsNullOrWhiteSpace(FromTpm))
        {
            return FromTpm;
        }

        return TryBuildShortNameFromMsBuildTriplet(
            EvaluatedProject.GetPropertyValue("TargetFrameworkIdentifier"),
            EvaluatedProject.GetPropertyValue("TargetFrameworkVersion"),
            EvaluatedProject.GetPropertyValue("TargetFrameworkProfile"));
    }

    /// <summary>
    /// When <paramref name="Raw"/> is a target-framework moniker string (e.g. <c>.NETCoreApp,Version=v2.0</c>), converts it to a NuGet folder name; otherwise returns trimmed <paramref name="Raw"/>.
    /// </summary>
    public static string? TryNormalizeTpmOrMonikerToShortName(string? Raw)
    {
        if (string.IsNullOrWhiteSpace(Raw))
        {
            return null;
        }

        string Trimmed = Raw.Trim();
        if (!Trimmed.Contains(',', StringComparison.Ordinal))
        {
            return Trimmed;
        }

        try
        {
            FrameworkName MsBuildFrameworkName = new FrameworkName(Trimmed);
            NuGetFramework ParsedFramework = new NuGetFramework(MsBuildFrameworkName.Identifier, MsBuildFrameworkName.Version, MsBuildFrameworkName.Profile ?? string.Empty);
            if (ParsedFramework.IsSpecificFramework)
            {
                return ParsedFramework.GetShortFolderName();
            }
        }
        catch (ArgumentException)
        {
        }

        return null;
    }

    /// <summary>
    /// Builds a short folder moniker from MSBuild target framework parts when <c>TargetFramework</c> is not set in the file.
    /// </summary>
    public static string? TryBuildShortNameFromMsBuildTriplet(string? Identifier, string? FrameworkVersionString, string? Profile)
    {
        if (string.IsNullOrWhiteSpace(Identifier))
        {
            return null;
        }

        string VersionText = string.IsNullOrWhiteSpace(FrameworkVersionString)
            ? string.Empty
            : FrameworkVersionString.Trim().TrimStart('v', 'V');
        if (!System.Version.TryParse(VersionText, out System.Version? ParsedVersion))
        {
            return null;
        }

        string ProfileText = string.IsNullOrWhiteSpace(Profile) ? string.Empty : Profile.Trim();

        try
        {
            NuGetFramework ParsedFramework = new NuGetFramework(Identifier.Trim(), ParsedVersion, ProfileText);
            if (ParsedFramework.IsSpecificFramework)
            {
                return ParsedFramework.GetShortFolderName();
            }
        }
        catch (ArgumentException)
        {
        }

        return null;
    }

    private static string? FirstNonEmptyLiteralText(XDocument Document, string LocalName)
    {
        foreach (XElement Element in Document.Descendants())
        {
            if (!string.Equals(Element.Name.LocalName, LocalName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string Text = Element.Value.Trim();
            if (string.IsNullOrWhiteSpace(Text))
            {
                continue;
            }

            if (Text.Contains('$', StringComparison.Ordinal))
            {
                continue;
            }

            return Text;
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] Values)
    {
        foreach (string? Value in Values)
        {
            if (!string.IsNullOrWhiteSpace(Value))
            {
                return Value;
            }
        }

        return null;
    }
}
