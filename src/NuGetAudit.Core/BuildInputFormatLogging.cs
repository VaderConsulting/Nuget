using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NuGetAudit.Core;

/// <summary>
/// Emits trace lines describing solution/project file shapes and NuGet package configuration for diagnostics.
/// </summary>
internal static class BuildInputFormatLogging
{
    /// <summary>
    /// Newest Visual Studio <strong>product year</strong> assumed when reporting VS-Max (opening older MSBuild/solution formats).
    /// </summary>
    private const int LATEST_VISUAL_STUDIO_OPEN_YEAR = 2026;

    private static string FormatSolutionLabel(string solutionDisplayName)
    {
        return $"Solution='{solutionDisplayName}'";
    }

    private static string FormatSolutionProjectLabel(string solutionDisplayName, string projectDisplayName)
    {
        return $"{FormatSolutionLabel(solutionDisplayName)} Project='{projectDisplayName}'";
    }

    /// <summary>
    /// Logs whether the audit input is a classic .sln, .slnx, or a single project file, including format/version hints when available.
    /// </summary>
    /// <param name="solutionDisplayName">The display name for the audit input (solution or single project), same as <see cref="SolutionInfo.SolutionName"/>.</param>
    /// <param name="inputPath">The path passed as the solution or project input.</param>
    public static void LogSolutionOrInputContainer(string solutionDisplayName, string inputPath)
    {
        string Extension = Path.GetExtension(inputPath);
        if (IsSupportedProjectExtension(Extension))
        {
            DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionProjectLabel(solutionDisplayName, solutionDisplayName)}: single project input ({Extension.TrimStart('.')}). Path='{inputPath}'.");
            return;
        }

        if (Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionLabel(solutionDisplayName)}: .slnx; {FormatVisualStudioOpenRange(2022, LATEST_VISUAL_STUDIO_OPEN_YEAR)}. Path='{inputPath}'.");
            return;
        }

        if (Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            LogClassicSolutionFileHeader(solutionDisplayName, inputPath);
            return;
        }

        DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionLabel(solutionDisplayName)}: unrecognized extension '{Extension}'. Path='{inputPath}'.");
    }

    /// <summary>
    /// Logs MSBuild project file shape (SDK-style vs legacy, tools version, declared .NET target frameworks).
    /// </summary>
    /// <param name="solutionDisplayName">The owning solution or audit input display name.</param>
    /// <param name="projectDisplayName">The project display name.</param>
    public static void LogProjectFileFormat(string solutionDisplayName, string projectDisplayName, XDocument projectDocument, string? targetFrameworksText)
    {
        XElement? Root = projectDocument.Root;
        if (Root is null || !string.Equals(Root.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
        {
            DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionProjectLabel(solutionDisplayName, projectDisplayName)}: not a standard MSBuild Project root.");
            return;
        }

        string? SdkValue = Root.Attribute("Sdk")?.Value?.Trim();
        string? ToolsVersion = Root.Attribute("ToolsVersion")?.Value?.Trim();

        List<string> Parts = new();
        if (!string.IsNullOrWhiteSpace(SdkValue))
        {
            Parts.Add($"Sdk={SdkValue}");
        }

        if (!string.IsNullOrWhiteSpace(ToolsVersion))
        {
            if (TryGetToolsVersionOpenYears(ToolsVersion, out int ProjectMinYear, out int ProjectMaxYear))
            {
                Parts.Add(FormatVisualStudioOpenRange(ProjectMinYear, ProjectMaxYear));
            }
            else
            {
                Parts.Add($"ToolsVersion={ToolsVersion}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(SdkValue))
        {
            Parts.Add(FormatVisualStudioOpenRange(2017, LATEST_VISUAL_STUDIO_OPEN_YEAR));
        }

        if (!string.IsNullOrWhiteSpace(targetFrameworksText))
        {
            Parts.Add($".NET version={targetFrameworksText}");
        }

        if (Parts.Count == 0)
        {
            return;
        }

        DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionProjectLabel(solutionDisplayName, projectDisplayName)}: Project file: {string.Join("; ", Parts)}.");
    }

    /// <summary>
    /// Logs detected NuGet package configuration (PackageReference vs packages.config, central management, lock and assets files).
    /// </summary>
    /// <param name="solutionDisplayName">The owning solution or audit input display name.</param>
    /// <param name="projectDisplayName">The project display name.</param>
    public static void LogPackageConfiguration(string solutionDisplayName, string projectDisplayName, string projectDirectory, ProjectStyle projectStyle, DirectoryPackagesPropsContext centralContext, XDocument projectDocument)
    {
        bool HasPackageReferenceElements = projectDocument.Descendants().Any(static element => element.Name.LocalName == "PackageReference");
        bool HasPackageVersionInProject = projectDocument.Descendants().Any(static element =>
            element.Name.LocalName == "PackageVersion"
            && (!string.IsNullOrWhiteSpace(element.Attribute("Include")?.Value)
                || !string.IsNullOrWhiteSpace(element.Attribute("Update")?.Value)));
        bool PackagesConfigOnDisk = File.Exists(Path.Combine(projectDirectory, "packages.config"));
        bool LockFilePresent = File.Exists(Path.Combine(projectDirectory, "packages.lock.json"));
        bool AssetsPresent = File.Exists(Path.Combine(projectDirectory, "obj", "project.assets.json"));

        List<string> Parts = new();
        Parts.Add($"Style={projectStyle}");

        if (HasPackageReferenceElements)
        {
            Parts.Add("PackageReference");
        }

        if (HasPackageVersionInProject)
        {
            Parts.Add("PackageVersion");
        }

        if (PackagesConfigOnDisk)
        {
            Parts.Add("packages.config");
        }

        IReadOnlyList<string> PropsPaths = centralContext.DirectoryPackagesPropsPathsNearestFirst;
        if (PropsPaths.Count > 0)
        {
            Parts.Add(PropsPaths.Count == 1 ? "Directory.Packages.props" : $"Directory.Packages.props ({PropsPaths.Count} files)");
            if (centralContext.CentralPackageVersionEntryCount > 0)
            {
                Parts.Add($"central versions={centralContext.CentralPackageVersionEntryCount}");
            }
        }

        if (centralContext.ManagePackageVersionsCentrally == true)
        {
            Parts.Add("ManagePackageVersionsCentrally");
        }

        if (LockFilePresent)
        {
            Parts.Add("packages.lock.json");
        }

        if (AssetsPresent)
        {
            Parts.Add("project.assets.json");
        }

        DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionProjectLabel(solutionDisplayName, projectDisplayName)}: Package configuration: {string.Join("; ", Parts)}.");
    }

    private static void LogClassicSolutionFileHeader(string solutionDisplayName, string solutionPath)
    {
        string? FormatSummary = null;
        string? VisualStudioSummary = null;
        string? VisualStudioVersionProperty = null;
        string? MinimumVisualStudioVersionProperty = null;
        int LineIndex = 0;
        try
        {
            foreach (string Line in File.ReadLines(solutionPath))
            {
                if (LineIndex++ > 40)
                {
                    break;
                }

                string Trimmed = Line.Trim();
                if (Trimmed.StartsWith("Microsoft Visual Studio Solution File,", StringComparison.Ordinal))
                {
                    int Idx = Trimmed.IndexOf("Format Version", StringComparison.OrdinalIgnoreCase);
                    FormatSummary = Idx >= 0 ? Trimmed.Substring(Idx).Trim() : Trimmed;
                }
                else if (Trimmed.StartsWith("# Visual Studio", StringComparison.Ordinal))
                {
                    VisualStudioSummary = Trimmed.TrimStart('#').Trim();
                }
                else if (Trimmed.StartsWith("VisualStudioVersion", StringComparison.Ordinal))
                {
                    int Eq = Trimmed.IndexOf('=');
                    if (Eq >= 0 && Eq < Trimmed.Length - 1)
                    {
                        VisualStudioVersionProperty = Trimmed[(Eq + 1)..].Trim();
                    }
                }
                else if (Trimmed.StartsWith("MinimumVisualStudioVersion", StringComparison.Ordinal))
                {
                    int Eq = Trimmed.IndexOf('=');
                    if (Eq >= 0 && Eq < Trimmed.Length - 1)
                    {
                        MinimumVisualStudioVersionProperty = Trimmed[(Eq + 1)..].Trim();
                    }
                }
            }
        }
        catch (Exception Exception)
        {
            DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionLabel(solutionDisplayName)}: classic .sln read failed ('{solutionPath}'): {Exception.GetType().Name}: {Exception.Message}");
            return;
        }

        List<string> Parts = new();
        Parts.Add("classic .sln");
        if (!string.IsNullOrWhiteSpace(FormatSummary))
        {
            string FormatEra = DescribeSolutionFormatVersionIntroduced(FormatSummary);
            Parts.Add(string.IsNullOrWhiteSpace(FormatEra) ? FormatSummary : $"{FormatSummary} ({FormatEra})");
        }

        if (!string.IsNullOrWhiteSpace(VisualStudioVersionProperty))
        {
            string? FromProp = MapVisualStudioVersionStringToShortProduct(VisualStudioVersionProperty);
            Parts.Add(string.IsNullOrWhiteSpace(FromProp)
                ? $"VisualStudioVersion={VisualStudioVersionProperty}"
                : $"VisualStudioVersion={VisualStudioVersionProperty} ({FromProp})");
        }
        else if (!string.IsNullOrWhiteSpace(VisualStudioSummary)
            && TryParseVisualStudioBannerMajor(VisualStudioSummary, out int BannerMajor))
        {
            Parts.Add(MapVisualStudioMajorToShortProduct(BannerMajor));
        }

        int SolutionMinYear = 0;
        if (!string.IsNullOrWhiteSpace(FormatSummary)
            && TryParseSolutionFormatVersion(FormatSummary, out int FormatMajor, out int FormatMinor))
        {
            int FromFormat = MapSolutionFileFormatToMinOpenYear(FormatMajor, FormatMinor);
            if (FromFormat > 0)
            {
                SolutionMinYear = Math.Max(SolutionMinYear, FromFormat);
            }
        }

        if (!string.IsNullOrWhiteSpace(MinimumVisualStudioVersionProperty)
            && Version.TryParse(MinimumVisualStudioVersionProperty, out Version? MinimumVs)
            && TryMapVisualStudioIdeMajorToMinOpenYear(MinimumVs.Major, out int FromMinimumProp))
        {
            SolutionMinYear = Math.Max(SolutionMinYear, FromMinimumProp);
        }

        if (SolutionMinYear > 0)
        {
            Parts.Add(FormatVisualStudioOpenRange(SolutionMinYear, LATEST_VISUAL_STUDIO_OPEN_YEAR));
        }

        DebugLog.WriteTrace("BuildFormat", $"{FormatSolutionLabel(solutionDisplayName)}: {string.Join("; ", Parts)}. Path='{solutionPath}'.");
    }

    private static string FormatVisualStudioOpenRange(int minYear, int maxYear)
    {
        return $"VS-Min: {minYear}; VS-Max: {maxYear}";
    }

    private static bool TryGetToolsVersionOpenYears(string toolsVersion, out int minYear, out int maxYear)
    {
        maxYear = LATEST_VISUAL_STUDIO_OPEN_YEAR;
        minYear = 0;
        if (string.Equals(toolsVersion, "Current", StringComparison.OrdinalIgnoreCase))
        {
            minYear = 2017;
            return true;
        }

        if (!Version.TryParse(toolsVersion, out Version? Parsed))
        {
            return false;
        }

        return TryMapMsBuildToolsVersionMajorToMinOpenYear(Parsed.Major, out minYear);
    }

    /// <summary>
    /// MSBuild <c>ToolsVersion</c> major (2, 3, 4, 12, 14, …) → first Visual Studio <strong>product year</strong> that shipped that toolset.
    /// </summary>
    private static bool TryMapMsBuildToolsVersionMajorToMinOpenYear(int toolsMajor, out int minYear)
    {
        minYear = toolsMajor switch
        {
            2 => 2005,
            3 => 2008,
            4 => 2010,
            12 => 2013,
            14 => 2015,
            15 => 2017,
            16 => 2019,
            17 => 2022,
            18 => 2026,
            _ => 0
        };

        if (toolsMajor > 18)
        {
            minYear = LATEST_VISUAL_STUDIO_OPEN_YEAR;
        }

        return minYear > 0;
    }

    /// <summary>
    /// Visual Studio IDE version major from <c>MinimumVisualStudioVersion</c> (10, 11, 12, 14, …) → first product year.
    /// </summary>
    private static bool TryMapVisualStudioIdeMajorToMinOpenYear(int ideMajor, out int minYear)
    {
        minYear = ideMajor switch
        {
            8 => 2005,
            9 => 2008,
            10 => 2010,
            11 => 2012,
            12 => 2013,
            14 => 2015,
            15 => 2017,
            16 => 2019,
            17 => 2022,
            18 => 2026,
            _ => 0
        };

        if (ideMajor > 18)
        {
            minYear = LATEST_VISUAL_STUDIO_OPEN_YEAR;
        }

        return minYear > 0;
    }

    private static bool TryParseSolutionFormatVersion(string formatSummaryLine, out int formatMajor, out int formatMinor)
    {
        formatMajor = 0;
        formatMinor = 0;
        Match Match = Regex.Match(formatSummaryLine, @"Format\s+Version\s+(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        if (!Match.Success)
        {
            return false;
        }

        formatMajor = int.Parse(Match.Groups[1].Value, CultureInfo.InvariantCulture);
        formatMinor = int.Parse(Match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Text solution <c>Format Version</c> (not the same numbering as MSBuild ToolsVersion) → minimum product year to open that file layout.
    /// </summary>
    private static int MapSolutionFileFormatToMinOpenYear(int formatMajor, int formatMinor)
    {
        if (formatMajor == 12 && formatMinor == 0)
        {
            return 2012;
        }

        if (formatMajor == 11)
        {
            return 2010;
        }

        if (formatMajor == 10)
        {
            return 2005;
        }

        return 0;
    }

    /// <summary>
    /// Short label for the solution "Format Version x.yy" line (introducing Visual Studio era).
    /// </summary>
    private static string DescribeSolutionFormatVersionIntroduced(string formatSummaryLine)
    {
        if (!TryParseSolutionFormatVersion(formatSummaryLine, out int Major, out int Minor))
        {
            return string.Empty;
        }

        if (Major == 12 && Minor == 0)
        {
            return "VS 2012+";
        }

        if (Major == 11)
        {
            return "VS 2010-era";
        }

        if (Major == 10)
        {
            return "pre-VS 2010";
        }

        return $"fmt {Major}.{Minor:00}";
    }

    private static string? MapVisualStudioVersionStringToShortProduct(string visualStudioVersionProperty)
    {
        int Dot = visualStudioVersionProperty.IndexOf('.');
        string MajorText = Dot > 0 ? visualStudioVersionProperty[..Dot] : visualStudioVersionProperty;
        if (!int.TryParse(MajorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int Major))
        {
            return null;
        }

        return MapVisualStudioMajorToShortProduct(Major);
    }

    private static bool TryParseVisualStudioBannerMajor(string visualStudioBannerLine, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(visualStudioBannerLine))
        {
            return false;
        }

        Match Match = Regex.Match(visualStudioBannerLine, @"Version\s+(\d+)\s*$", RegexOptions.IgnoreCase);
        if (Match.Success)
        {
            return int.TryParse(Match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
        }

        Match = Regex.Match(visualStudioBannerLine, @"Visual\s+Studio\s+(\d+)\s*$", RegexOptions.IgnoreCase);
        if (Match.Success)
        {
            return int.TryParse(Match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
        }

        return false;
    }

    private static string MapVisualStudioMajorToShortProduct(int major)
    {
        return major switch
        {
            10 => "VS 2010",
            11 => "VS 2012",
            12 => "VS 2013",
            14 => "VS 2015",
            15 => "VS 2017",
            16 => "VS 2019",
            17 => "VS 2022",
            18 => "VS 2026",
            >= 19 => $"VS major {major}",
            _ => $"VS legacy ({major})"
        };
    }

    private static bool IsSupportedProjectExtension(string extension)
    {
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);
    }
}
