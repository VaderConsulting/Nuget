using System.Xml.Linq;

namespace NuGetAudit.Core;

/// <summary>
/// Reads central package versions from <c>Directory.Packages.props</c> and from <c>PackageVersion</c> items in the project file.
/// </summary>
internal sealed class DirectoryPackagesPropsContext
{
    private readonly Dictionary<string, string> _versions;
    private readonly List<string> _directoryPackagesPropsPathsNearestFirst;

    private DirectoryPackagesPropsContext(Dictionary<string, string> versions, List<string> directoryPackagesPropsPathsNearestFirst, bool? managePackageVersionsCentrally)
    {
        _versions = versions;
        _directoryPackagesPropsPathsNearestFirst = directoryPackagesPropsPathsNearestFirst;
        ManagePackageVersionsCentrally = managePackageVersionsCentrally;
    }

    /// <summary>
    /// Gets <c>Directory.Packages.props</c> files that contributed central versions, ordered from the project directory upward.
    /// </summary>
    public IReadOnlyList<string> DirectoryPackagesPropsPathsNearestFirst
    {
        get
        {
            return _directoryPackagesPropsPathsNearestFirst;
        }
    }

    /// <summary>
    /// Gets the value of <c>ManagePackageVersionsCentrally</c> from the first <c>Directory.Packages.props</c> encountered when walking from the project directory toward the drive root that defines the property.
    /// </summary>
    public bool? ManagePackageVersionsCentrally { get; }

    /// <summary>
    /// Gets the number of distinct package ids with central versions after merging props and project <c>PackageVersion</c> items.
    /// </summary>
    public int CentralPackageVersionEntryCount
    {
        get
        {
            return _versions.Count;
        }
    }

    /// <summary>
    /// Loads central <c>PackageVersion</c> entries by walking from the project directory up to the filesystem root for
    /// <c>Directory.Packages.props</c>, then overlays any <c>PackageVersion</c> items from the project document.
    /// </summary>
    /// <param name="startDirectory">The directory where the search begins.</param>
    /// <param name="projectDocument">The evaluated project XML, when available.</param>
    /// <returns>The loaded central package context.</returns>
    public static DirectoryPackagesPropsContext Load(string startDirectory, XDocument? projectDocument)
    {
        Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
        List<string> propsPathsNearestFirst = new();
        bool? managePackageVersionsCentrally = null;
        DirectoryInfo? current = new(startDirectory);

        while (current is not null)
        {
            string propsPath = Path.Combine(current.FullName, "Directory.Packages.props");
            if (File.Exists(propsPath))
            {
                string fullPropsPath = Path.GetFullPath(propsPath);
                if (!propsPathsNearestFirst.Contains(fullPropsPath, StringComparer.OrdinalIgnoreCase))
                {
                    propsPathsNearestFirst.Add(fullPropsPath);
                }

                XDocument document = XDocument.Load(propsPath);

                bool? fromFile = TryReadManagePackageVersionsCentrally(document);
                if (fromFile is not null && managePackageVersionsCentrally is null)
                {
                    managePackageVersionsCentrally = fromFile;
                }

                foreach (XElement packageVersion in document.Descendants().Where(static element => string.Equals(element.Name.LocalName, "PackageVersion", StringComparison.OrdinalIgnoreCase)))
                {
                    string? packageId = GetAttributeValueIgnoreCase(packageVersion, "Include");
                    string? version = GetAttributeValueIgnoreCase(packageVersion, "Version");

                    if (!string.IsNullOrWhiteSpace(packageId) && !string.IsNullOrWhiteSpace(version))
                    {
                        versions[packageId] = version;
                    }
                }
            }

            current = current.Parent;
        }

        if (projectDocument is not null)
        {
            foreach (XElement packageVersion in projectDocument.Descendants().Where(static element => string.Equals(element.Name.LocalName, "PackageVersion", StringComparison.OrdinalIgnoreCase)))
            {
                string? packageId = GetAttributeValueIgnoreCase(packageVersion, "Include") ?? GetAttributeValueIgnoreCase(packageVersion, "Update");
                string? version = GetAttributeValueIgnoreCase(packageVersion, "Version");

                if (!string.IsNullOrWhiteSpace(packageId) && !string.IsNullOrWhiteSpace(version))
                {
                    versions[packageId] = version;
                }
            }
        }

        return new DirectoryPackagesPropsContext(versions, propsPathsNearestFirst, managePackageVersionsCentrally);
    }

    private static string? GetAttributeValueIgnoreCase(XElement Element, string LocalName)
    {
        return Element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, LocalName, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static bool? TryReadManagePackageVersionsCentrally(XDocument propsDocument)
    {
        foreach (XElement element in propsDocument.Descendants())
        {
            if (!string.Equals(element.Name.LocalName, "ManagePackageVersionsCentrally", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? value = element.Value?.Trim();
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to get a centrally managed version for the specified package.
    /// </summary>
    /// <param name="packageId">The package identifier.</param>
    /// <param name="version">When this method returns, contains the discovered version when found.</param>
    /// <returns><see langword="true"/> when a version was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGetVersion(string packageId, out string? version)
    {
        bool found = _versions.TryGetValue(packageId, out string? storedVersion);
        version = storedVersion;
        return found;
    }
}
