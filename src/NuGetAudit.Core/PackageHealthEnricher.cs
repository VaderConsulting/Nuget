using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace NuGetAudit.Core;

/// <summary>
/// Enriches package records with deprecation, obsolescence, outdated-version, and vulnerability information.
/// </summary>
internal sealed class PackageHealthEnricher
{
    private static readonly Uri ServiceIndexUri = new("https://api.nuget.org/v3/index.json");
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private const int PROJECT_URL_PROBE_MAX_CONCURRENCY = 2;
    private const int PROJECT_URL_PROBE_TIMEOUT_SECONDS = 8;

    private string? _registrationBaseUrl;

    private enum RegistrationHttpResultKind
    {
        Ok,
        NotFound,
        TransportError,
        NonSuccessStatus
    }

    private readonly record struct RegistrationHttpResult(
        RegistrationHttpResultKind Kind,
        string? Body,
        HttpStatusCode? StatusCode,
        Exception? Exception);

    /// <summary>
    /// Enriches the supplied project snapshots with package health metadata.
    /// </summary>
    /// <param name="projects">The project snapshots to enrich.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The enriched projects and any generated warnings.</returns>
    public async Task<PackageHealthEnrichmentResult> EnrichAsync(IReadOnlyList<ProjectSnapshot> projects, CancellationToken cancellationToken)
    {
        List<string> warnings = new();
        HashSet<string> transportWarningKeys = new(StringComparer.OrdinalIgnoreCase);

        List<string> distinctPackageIds = projects
            .SelectMany(static project => project.Packages)
            .Select(static package => package.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? registrationBaseUrl = await TryResolveRegistrationBaseUrlAsync(cancellationToken);
        Dictionary<string, PackageRegistrationInfo> registrationLookup = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(registrationBaseUrl))
        {
            warnings.Add("Package health enrichment was limited: NuGet service index (api.nuget.org) could not be reached or did not list a registrations URL. Check network connectivity, proxy, and firewall rules.");
            foreach (string packageId in distinctPackageIds)
            {
                registrationLookup[packageId] = new PackageRegistrationInfo(packageId, Array.Empty<PackageRegistrationLeafInfo>(), RegistrationLoadOutcome.Failed);
            }
        }
        else
        {
            foreach (string packageId in distinctPackageIds)
            {
                PackageRegistrationInfo registrationInfo = await TryLoadRegistrationAsync(
                    registrationBaseUrl,
                    packageId,
                    warnings,
                    transportWarningKeys,
                    cancellationToken);
                registrationLookup[packageId] = registrationInfo;
            }
        }

        await ApplyProjectUrlReachabilityAsync(registrationLookup, warnings, transportWarningKeys, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ProjectSnapshot> enrichedProjects = projects
            .Select(project => project with
            {
                Packages = project.Packages
                    .Select(package => package with
                    {
                        HealthInfo = CreateHealthInfo(package, registrationLookup[package.PackageId])
                    })
                    .ToArray()
            })
            .Select(project => project with
            {
                Diagnostics = BuildDiagnostics(project)
            })
            .ToArray();

        return new PackageHealthEnrichmentResult(enrichedProjects, warnings);
    }

    /// <summary>
    /// Probes distinct <c>projectUrl</c> values from loaded registration leaves and fills <see cref="PackageRegistrationLeafInfo.ProjectUrlReachable"/>.
    /// </summary>
    private static async Task ApplyProjectUrlReachabilityAsync(
        Dictionary<string, PackageRegistrationInfo> registrationLookup,
        List<string> warnings,
        HashSet<string> transportWarningKeys,
        CancellationToken cancellationToken)
    {
        HashSet<string> distinctUrls = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageRegistrationInfo info in registrationLookup.Values)
        {
            if (info.LoadOutcome != RegistrationLoadOutcome.Loaded)
            {
                continue;
            }

            foreach (PackageRegistrationLeafInfo leaf in info.Versions)
            {
                string? normalized = NormalizeProjectUrlForProbe(leaf.ProjectUrl);
                if (normalized is not null)
                {
                    distinctUrls.Add(normalized);
                }
            }
        }

        if (distinctUrls.Count == 0)
        {
            return;
        }

        ConcurrentDictionary<string, bool> probeResults = new(StringComparer.OrdinalIgnoreCase);
        bool probeErrorLogged = false;
        using SemaphoreSlim gate = new(PROJECT_URL_PROBE_MAX_CONCURRENCY, PROJECT_URL_PROBE_MAX_CONCURRENCY);
        IEnumerable<Task> probeTasks = distinctUrls.Select(async url =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool reachable = await ProbeProjectUrlReachableAsync(url, cancellationToken).ConfigureAwait(false);
                probeResults[url] = reachable;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                probeResults[url] = false;
                if (!probeErrorLogged && transportWarningKeys.Add("project-url-probe"))
                {
                    probeErrorLogged = true;
                    warnings.Add("One or more NuGet package project URL reachability checks failed (network, timeout, or TLS). Abandoned-package inference may be less accurate.");
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probeTasks).ConfigureAwait(false);

        foreach (string packageId in registrationLookup.Keys.ToArray())
        {
            PackageRegistrationInfo info = registrationLookup[packageId];
            if (info.LoadOutcome != RegistrationLoadOutcome.Loaded || info.Versions.Count == 0)
            {
                continue;
            }

            PackageRegistrationLeafInfo[] updated = info.Versions
                .Select(leaf =>
                {
                    string? key = NormalizeProjectUrlForProbe(leaf.ProjectUrl);
                    bool? reachable = key is null ? null : probeResults.TryGetValue(key, out bool value) ? value : null;
                    return leaf with { ProjectUrlReachable = reachable };
                })
                .ToArray();
            registrationLookup[packageId] = info with { Versions = updated };
        }
    }

    /// <summary>
    /// Returns an absolute http(s) URL string for deduplication and probing, or <see langword="null"/> when unsuitable.
    /// </summary>
    private static string? NormalizeProjectUrlForProbe(string? projectUrl)
    {
        if (string.IsNullOrWhiteSpace(projectUrl))
        {
            return null;
        }

        string trimmed = projectUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// Attempts HEAD, then GET on 405, to see whether the project page responds without downloading the body.
    /// </summary>
    private static async Task<bool> ProbeProjectUrlReachableAsync(string requestUrl, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(PROJECT_URL_PROBE_TIMEOUT_SECONDS));
        CancellationToken probeToken = linked.Token;
        try
        {
            using HttpRequestMessage headRequest = new(HttpMethod.Head, requestUrl);
            using HttpResponseMessage headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, probeToken).ConfigureAwait(false);
            int headCode = (int)headResponse.StatusCode;
            if (headCode == 405 || headCode == 501)
            {
                using HttpRequestMessage getRequest = new(HttpMethod.Get, requestUrl);
                using HttpResponseMessage getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, probeToken).ConfigureAwait(false);
                int getCode = (int)getResponse.StatusCode;
                return getCode >= 200 && getCode < 400;
            }

            return headCode >= 200 && headCode < 400;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads optional <c>projectUrl</c> from the NuGet catalog entry JSON.
    /// </summary>
    private static string? TryParseProjectUrl(JsonElement catalogEntry)
    {
        if (!catalogEntry.TryGetProperty("projectUrl", out JsonElement projectUrlElement))
        {
            return null;
        }

        string? text = projectUrlElement.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Resolves and caches the NuGet registrations base URL, or returns <see langword="null"/> when the service index is unreachable or unusable.
    /// </summary>
    private async Task<string?> TryResolveRegistrationBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_registrationBaseUrl))
        {
            return _registrationBaseUrl;
        }

        RegistrationHttpResult indexResponse = await SendGetAsync(
            ServiceIndexUri.ToString(),
            packageId: null,
            "NuGet service index request",
            logNotFoundAsHttpFailure: true,
            cancellationToken).ConfigureAwait(false);

        if (indexResponse.Kind != RegistrationHttpResultKind.Ok || string.IsNullOrEmpty(indexResponse.Body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(indexResponse.Body);
            JsonElement resources = document.RootElement.GetProperty("resources");

            foreach (JsonElement resource in resources.EnumerateArray())
            {
                string? type = resource.GetProperty("@type").GetString();

                if (string.Equals(type, "RegistrationsBaseUrl/3.6.0", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type, "RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase))
                {
                    _registrationBaseUrl = resource.GetProperty("@id").GetString();
                    break;
                }
            }
        }
        catch (JsonException exception)
        {
            LogPackageHealthJsonFailure("NuGet service index response", requestUri: ServiceIndexUri.ToString(), packageId: null, exception);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_registrationBaseUrl))
        {
            string detail = "NuGet service index response did not include a RegistrationsBaseUrl resource.";
            DebugLog.WriteTrace("PackageHealth", detail);
            return null;
        }

        return _registrationBaseUrl;
    }

    private static async Task<PackageRegistrationInfo> LoadRegistrationAsync(string registrationBaseUrl, string packageId, CancellationToken cancellationToken)
    {
        string lowerPackageId = packageId.ToLowerInvariant();
        string indexUrl = $"{registrationBaseUrl.TrimEnd('/')}/{lowerPackageId}/index.json";

        RegistrationHttpResult indexResponse = await SendGetAsync(
            indexUrl,
            packageId,
            "NuGet registration index request",
            logNotFoundAsHttpFailure: false,
            cancellationToken).ConfigureAwait(false);

        if (indexResponse.Kind == RegistrationHttpResultKind.NotFound)
        {
            return new PackageRegistrationInfo(packageId, Array.Empty<PackageRegistrationLeafInfo>(), RegistrationLoadOutcome.NotFound);
        }

        if (indexResponse.Kind == RegistrationHttpResultKind.TransportError)
        {
            throw indexResponse.Exception!;
        }

        if (indexResponse.Kind == RegistrationHttpResultKind.NonSuccessStatus)
        {
            throw indexResponse.Exception!;
        }

        string indexJson = indexResponse.Body ?? string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(indexJson);
        }
        catch (JsonException exception)
        {
            LogPackageHealthJsonFailure("NuGet registration index JSON", indexUrl, packageId, exception);
            throw;
        }

        using (document)
        {
            List<PackageRegistrationLeafInfo> leaves = new();

            foreach (JsonElement page in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (page.TryGetProperty("items", out JsonElement pageItems))
                {
                    leaves.AddRange(ParseLeaves(pageItems));
                    continue;
                }

                string pageUrl = page.GetProperty("@id").GetString()!;
                RegistrationHttpResult pageResponse = await SendGetAsync(
                    pageUrl,
                    packageId,
                    "NuGet registration page request",
                    logNotFoundAsHttpFailure: true,
                    cancellationToken).ConfigureAwait(false);

                if (pageResponse.Kind == RegistrationHttpResultKind.NotFound)
                {
                    throw new HttpRequestException($"NuGet registration leaf page returned HTTP 404 (unexpected after a successful registration index). PackageId='{packageId}'. Request='{pageUrl}'.");
                }

                if (pageResponse.Kind == RegistrationHttpResultKind.TransportError)
                {
                    throw pageResponse.Exception!;
                }

                if (pageResponse.Kind == RegistrationHttpResultKind.NonSuccessStatus)
                {
                    throw pageResponse.Exception!;
                }

                string pageJson = pageResponse.Body ?? string.Empty;

                JsonDocument pageDocument;
                try
                {
                    pageDocument = JsonDocument.Parse(pageJson);
                }
                catch (JsonException exception)
                {
                    LogPackageHealthJsonFailure("NuGet registration page JSON", pageUrl, packageId, exception);
                    throw;
                }

                using (pageDocument)
                {
                    leaves.AddRange(ParseLeaves(pageDocument.RootElement.GetProperty("items")));
                }
            }

            return new PackageRegistrationInfo(packageId, leaves, RegistrationLoadOutcome.Loaded);
        }
    }

    /// <summary>
    /// Performs GET with <see cref="HttpClient.SendAsync"/> so HTTP status codes are handled without throwing for expected 404 (registration index only).
    /// </summary>
    private static async Task<RegistrationHttpResult> SendGetAsync(string requestUri, string? packageId, string stage, bool logNotFoundAsHttpFailure, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            LogPackageHealthHttpFailure(stage, packageId, requestUri, exception);
            return new RegistrationHttpResult(RegistrationHttpResultKind.TransportError, null, null, exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (logNotFoundAsHttpFailure)
                {
                    HttpRequestException notFoundException = new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).", null, HttpStatusCode.NotFound);
                    LogPackageHealthHttpFailure(stage, packageId, requestUri, notFoundException);
                }

                return new RegistrationHttpResult(RegistrationHttpResultKind.NotFound, null, HttpStatusCode.NotFound, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                HttpRequestException failureException = new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
                LogPackageHealthHttpFailure(stage, packageId, requestUri, failureException);
                return new RegistrationHttpResult(RegistrationHttpResultKind.NonSuccessStatus, null, response.StatusCode, failureException);
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new RegistrationHttpResult(RegistrationHttpResultKind.Ok, body, response.StatusCode, null);
        }
    }

    private static async Task<PackageRegistrationInfo> TryLoadRegistrationAsync(string registrationBaseUrl, string packageId, List<string> warnings, HashSet<string> transportWarningKeys, CancellationToken cancellationToken)
    {
        try
        {
            PackageRegistrationInfo registration = await LoadRegistrationAsync(registrationBaseUrl, packageId, cancellationToken).ConfigureAwait(false);
            if (registration.LoadOutcome == RegistrationLoadOutcome.NotFound)
            {
                warnings.Add($"Package health metadata was not available for '{packageId}' from nuget.org.");
            }

            return registration;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            RecordTransportWarning(warnings, transportWarningKeys);
            return new PackageRegistrationInfo(packageId, Array.Empty<PackageRegistrationLeafInfo>(), RegistrationLoadOutcome.Failed);
        }
    }

    private static void LogPackageHealthHttpFailure(string stage, string? packageId, string requestUri, Exception exception)
    {
        string statusSuffix = string.Empty;
        if (exception is HttpRequestException httpException && httpException.StatusCode.HasValue)
        {
            HttpStatusCode code = httpException.StatusCode.Value;
            statusSuffix = $" HTTP {(int)code} {code}";
        }

        string packageSuffix = string.IsNullOrWhiteSpace(packageId) ? string.Empty : $" PackageId='{packageId}'.";
        string detail = $"{stage}{statusSuffix}.{packageSuffix} Request='{requestUri}'. {exception.GetType().Name}: {exception.Message}";
        DebugLog.WriteTrace("PackageHealth", detail);
    }

    private static void LogPackageHealthJsonFailure(string stage, string requestUri, string? packageId, JsonException exception)
    {
        string packageSuffix = string.IsNullOrWhiteSpace(packageId) ? string.Empty : $" PackageId='{packageId}'.";
        string detail = $"{stage}.{packageSuffix} Request='{requestUri}'. {exception.GetType().Name}: {exception.Message}";
        DebugLog.WriteTrace("PackageHealth", detail);
    }

    private static void RecordTransportWarning(List<string> warnings, HashSet<string> transportWarningKeys)
    {
        if (!transportWarningKeys.Add("nuget-registration-transport"))
        {
            return;
        }

        warnings.Add("One or more NuGet.org registration requests failed (network, timeout, or TLS). Vulnerability and deprecation details may be missing for affected packages.");
    }

    private static IEnumerable<PackageRegistrationLeafInfo> ParseLeaves(JsonElement items)
    {
        foreach (JsonElement leaf in items.EnumerateArray())
        {
            JsonElement catalogEntry = leaf.GetProperty("catalogEntry");
            string version = catalogEntry.GetProperty("version").GetString() ?? string.Empty;
            PackageDeprecationInfo? deprecationInfo = TryParseDeprecation(catalogEntry);
            IReadOnlyList<PackageVulnerabilityRecord> vulnerabilities = ParseVulnerabilities(catalogEntry);

            string? projectUrl = TryParseProjectUrl(catalogEntry);
            yield return new PackageRegistrationLeafInfo(version, deprecationInfo, vulnerabilities, ParseSupportedFrameworks(catalogEntry), IsListed(catalogEntry), TryParseCatalogPublishedUtc(catalogEntry), projectUrl, null);
        }
    }

    private static DateTimeOffset? TryParseCatalogPublishedUtc(JsonElement catalogEntry)
    {
        if (!catalogEntry.TryGetProperty("published", out JsonElement publishedElement))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(publishedElement.GetString(), out DateTimeOffset publishedUtc))
        {
            return null;
        }

        if (publishedUtc.Year <= 1901)
        {
            return null;
        }

        return publishedUtc;
    }

    /// <summary>
    /// Determines whether a registration leaf represents a listed package version.
    /// </summary>
    /// <param name="catalogEntry">The package catalog entry.</param>
    /// <returns><see langword="true"/> when the version is listed; otherwise, <see langword="false"/>.</returns>
    private static bool IsListed(JsonElement catalogEntry)
    {
        if (catalogEntry.TryGetProperty("listed", out JsonElement listedElement)
            && listedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return listedElement.GetBoolean();
        }

        if (catalogEntry.TryGetProperty("published", out JsonElement publishedElement)
            && DateTimeOffset.TryParse(publishedElement.GetString(), out DateTimeOffset publishedUtc))
        {
            return publishedUtc.Year > 1901;
        }

        return true;
    }

    /// <summary>
    /// Parses target framework groups advertised for a package version.
    /// </summary>
    /// <param name="catalogEntry">The package catalog entry.</param>
    /// <returns>The declared supported framework monikers.</returns>
    private static IReadOnlyList<string> ParseSupportedFrameworks(JsonElement catalogEntry)
    {
        if (!catalogEntry.TryGetProperty("dependencyGroups", out JsonElement dependencyGroups))
        {
            return Array.Empty<string>();
        }

        return dependencyGroups.EnumerateArray()
            .Select(group => group.TryGetProperty("targetFramework", out JsonElement frameworkElement)
                ? frameworkElement.GetString()
                : null)
            .Where(static framework => !string.IsNullOrWhiteSpace(framework))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PackageDeprecationInfo? TryParseDeprecation(JsonElement catalogEntry)
    {
        if (!catalogEntry.TryGetProperty("deprecation", out JsonElement deprecation))
        {
            return null;
        }

        string? message = deprecation.TryGetProperty("message", out JsonElement messageElement)
            ? messageElement.GetString()
            : null;
        string[] reasons = deprecation.TryGetProperty("reasons", out JsonElement reasonsElement)
            ? reasonsElement.EnumerateArray().Select(static element => element.GetString() ?? string.Empty).Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray()
            : Array.Empty<string>();

        string? alternatePackageId = null;
        string? alternatePackageRange = null;

        if (deprecation.TryGetProperty("alternatePackage", out JsonElement alternatePackage))
        {
            if (alternatePackage.TryGetProperty("id", out JsonElement alternateId))
            {
                alternatePackageId = alternateId.GetString();
            }

            if (alternatePackage.TryGetProperty("range", out JsonElement alternateRange))
            {
                alternatePackageRange = alternateRange.GetString();
            }
        }

        return new PackageDeprecationInfo(message, reasons, alternatePackageId, alternatePackageRange);
    }

    private static IReadOnlyList<PackageVulnerabilityRecord> ParseVulnerabilities(JsonElement catalogEntry)
    {
        if (!catalogEntry.TryGetProperty("vulnerabilities", out JsonElement vulnerabilities))
        {
            return Array.Empty<PackageVulnerabilityRecord>();
        }

        return vulnerabilities.EnumerateArray()
            .Select(vulnerability => new PackageVulnerabilityRecord(vulnerability.TryGetProperty("advisoryUrl", out JsonElement advisoryUrl) ? advisoryUrl.GetString() ?? string.Empty : string.Empty, ParseSeverity(vulnerability.TryGetProperty("severity", out JsonElement severityElement) ? severityElement.GetString() : null)))
            .Where(static vulnerability => !string.IsNullOrWhiteSpace(vulnerability.AdvisoryUrl))
            .ToArray();
    }

    private static PackageHealthInfo CreateHealthInfo(PackageReferenceRecord package, PackageRegistrationInfo registration)
    {
        if (registration.LoadOutcome == RegistrationLoadOutcome.NotFound)
        {
            return PackageHealthInfo.None with { DevelopmentStatus = PackageDevelopmentStatus.Removed };
        }

        if (registration.LoadOutcome == RegistrationLoadOutcome.Failed)
        {
            string? versionTextFailed = package.ResolvedVersion ?? package.RequestedVersion;
            string? latestWhenFailed = registration.GetLatestCompatibleStableVersion(package.TargetFrameworkMoniker);
            if (string.IsNullOrWhiteSpace(versionTextFailed)
                || !NuGetVersion.TryParse(versionTextFailed, out NuGetVersion? unusedFailedVersion))
            {
                return PackageHealthInfo.None with { LatestStableVersion = latestWhenFailed };
            }

            return PackageHealthInfo.None with
            {
                LatestStableVersion = latestWhenFailed,
                DevelopmentStatus = PackageDevelopmentStatus.Unknown
            };
        }

        string? versionText = package.ResolvedVersion ?? package.RequestedVersion;

        if (string.IsNullOrWhiteSpace(versionText) || !NuGetVersion.TryParse(versionText, out NuGetVersion? currentVersion))
        {
            return PackageHealthInfo.None with
            {
                LatestStableVersion = registration.GetLatestCompatibleStableVersion(package.TargetFrameworkMoniker),
                DevelopmentStatus = PackageDevelopmentStatus.Unknown
            };
        }

        PackageRegistrationLeafInfo? matchedVersion = registration.FindVersion(currentVersion);
        NuGetVersion? latestStable = registration.GetLatestCompatibleStableNuGetVersion(package.TargetFrameworkMoniker, currentVersion);
        bool isOutdated = latestStable is not null && latestStable > currentVersion;
        bool isDeprecated = matchedVersion?.Deprecation is not null;
        bool isObsolete = isDeprecated && (
            matchedVersion!.Deprecation!.Reasons.Contains("Legacy", StringComparer.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(matchedVersion.Deprecation.AlternatePackageId));
        IReadOnlyList<PackageVulnerabilityRecord> vulnerabilities = matchedVersion?.Vulnerabilities ?? Array.Empty<PackageVulnerabilityRecord>();
        PackageVulnerabilitySeverity maxSeverity = vulnerabilities.Count == 0
            ? PackageVulnerabilitySeverity.None
            : vulnerabilities.Max(static vulnerability => vulnerability.Severity);

        PackageDevelopmentStatus developmentStatus = PackageDevelopmentStatus.Active;
        if (ShouldMarkAbandoned(registration, package.TargetFrameworkMoniker, currentVersion, matchedVersion, isOutdated))
        {
            developmentStatus = PackageDevelopmentStatus.Abandoned;
        }

        return new PackageHealthInfo(isDeprecated, isObsolete, isOutdated, vulnerabilities.Count > 0, latestStable?.ToNormalizedString(), matchedVersion?.Deprecation?.Message, matchedVersion?.Deprecation?.AlternatePackageId, matchedVersion?.Deprecation?.AlternatePackageRange, maxSeverity, vulnerabilities, developmentStatus);
    }

    private static bool ShouldMarkAbandoned(PackageRegistrationInfo registration, string? targetFrameworkMoniker, NuGetVersion currentVersion, PackageRegistrationLeafInfo? matchedVersion, bool isOutdated)
    {
        if (isOutdated || currentVersion.IsPrerelease)
        {
            return false;
        }

        NuGetVersion? latestStable = registration.GetLatestCompatibleStableNuGetVersion(targetFrameworkMoniker, currentVersion);
        if (latestStable is null || latestStable != currentVersion)
        {
            return false;
        }

        if (matchedVersion?.PublishedUtc is null)
        {
            return false;
        }

        if (matchedVersion.ProjectUrlReachable == true)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - matchedVersion.PublishedUtc.Value >= TimeSpan.FromDays(365);
    }

    private static PackageVulnerabilitySeverity ParseSeverity(string? severity)
    {
        return severity?.Trim().ToLowerInvariant() switch
        {
            "low" => PackageVulnerabilitySeverity.Low,
            "moderate" => PackageVulnerabilitySeverity.Moderate,
            "high" => PackageVulnerabilitySeverity.High,
            "critical" => PackageVulnerabilitySeverity.Critical,
            _ => PackageVulnerabilitySeverity.None
        };
    }

    internal sealed record PackageHealthEnrichmentResult(IReadOnlyList<ProjectSnapshot> Projects, IReadOnlyList<string> Warnings);

    private static IReadOnlyList<AuditDiagnostic> BuildDiagnostics(ProjectSnapshot project)
    {
        List<AuditDiagnostic> diagnostics = new();

        foreach (PackageReferenceRecord package in project.Packages.Where(static package => package.HealthInfo.RequiresAttention))
        {
            PackageHealthInfo health = package.HealthInfo;

            if (health.IsVulnerable)
            {
                AuditDiagnosticSeverity severity = health.MaxVulnerabilitySeverity is PackageVulnerabilitySeverity.High or PackageVulnerabilitySeverity.Critical
                    ? AuditDiagnosticSeverity.Error
                    : AuditDiagnosticSeverity.Warning;

                diagnostics.Add(new AuditDiagnostic("NUAUDVULN", severity, $"Package '{package.PackageId}' {FormatVersion(package)} has known {health.MaxVulnerabilitySeverity.ToString().ToLowerInvariant()} severity vulnerabilities.", project.ProjectName, project.ProjectPath, package.PackageId, package.TargetFrameworkMoniker, health.Vulnerabilities.FirstOrDefault()?.AdvisoryUrl));
            }

            if (health.IsDeprecated || health.IsObsolete)
            {
                string replacement = string.IsNullOrWhiteSpace(health.AlternatePackageId)
                    ? string.Empty
                    : $" Suggested replacement: {health.AlternatePackageId}{(string.IsNullOrWhiteSpace(health.AlternatePackageRange) ? string.Empty : $" {health.AlternatePackageRange}")}.";

                diagnostics.Add(new AuditDiagnostic("NUAUDDEPR", AuditDiagnosticSeverity.Warning, $"Package '{package.PackageId}' {FormatVersion(package)} is {(health.IsObsolete ? "obsolete" : "deprecated")}.{replacement}", project.ProjectName, project.ProjectPath, package.PackageId, package.TargetFrameworkMoniker, null));
            }

            if (health.IsOutdated && !string.IsNullOrWhiteSpace(health.LatestStableVersion))
            {
                diagnostics.Add(new AuditDiagnostic("NUAUDOLD", AuditDiagnosticSeverity.Warning, $"Package '{package.PackageId}' {FormatVersion(package)} is outdated. Latest compatible stable version: {health.LatestStableVersion}.", project.ProjectName, project.ProjectPath, package.PackageId, package.TargetFrameworkMoniker, null));
            }

            if (health.DevelopmentStatus == PackageDevelopmentStatus.Removed)
            {
                diagnostics.Add(new AuditDiagnostic("NUAUDREM", AuditDiagnosticSeverity.Warning, $"Package '{package.PackageId}' was not found on nuget.org (registration returned HTTP 404). Confirm the id, private feeds, or a replacement package.", project.ProjectName, project.ProjectPath, package.PackageId, package.TargetFrameworkMoniker, null));
            }

            if (health.DevelopmentStatus == PackageDevelopmentStatus.Abandoned)
            {
                diagnostics.Add(new AuditDiagnostic("NUAUDABN", AuditDiagnosticSeverity.Warning, $"Package '{package.PackageId}' {FormatVersion(package)}: latest compatible stable matches the resolved version, but that release was published more than a year ago on nuget.org.", project.ProjectName, project.ProjectPath, package.PackageId, package.TargetFrameworkMoniker, null));
            }
        }

        return diagnostics
            .OrderByDescending(static diagnostic => diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static diagnostic => diagnostic.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatVersion(PackageReferenceRecord package)
    {
        return package.ResolvedVersion ?? package.RequestedVersion ?? "(unknown version)";
    }

    private enum RegistrationLoadOutcome
    {
        Loaded,
        NotFound,
        Failed
    }

    private sealed record PackageRegistrationInfo(string PackageId, IReadOnlyList<PackageRegistrationLeafInfo> Versions, RegistrationLoadOutcome LoadOutcome)
    {
        public PackageRegistrationLeafInfo? FindVersion(NuGetVersion version)
        {
            return Versions.FirstOrDefault(candidate =>
                candidate.IsListed &&
                NuGetVersion.TryParse(candidate.Version, out NuGetVersion? candidateVersion)
                && candidateVersion == version);
        }

        /// <summary>
        /// Gets the newest stable version that appears compatible with the supplied target framework.
        /// </summary>
        /// <param name="targetFrameworkMoniker">The package target framework scope.</param>
        /// <returns>The latest compatible stable version, if one is known.</returns>
        public NuGetVersion? GetLatestCompatibleStableNuGetVersion(string? targetFrameworkMoniker, NuGetVersion? currentVersion = null)
        {
            return Versions
                .Where(static version => version.IsListed)
                .Where(version => SupportsFramework(version, targetFrameworkMoniker))
                .Where(version => SupportsUpgradePath(version, currentVersion))
                .Select(static version =>
                    NuGetVersion.TryParse(version.Version, out NuGetVersion? parsed) ? parsed : null)
                .Where(static version => version is not null && !version.IsPrerelease)
                .Cast<NuGetVersion>()
                .OrderByDescending(static version => version)
                .FirstOrDefault();
        }

        /// <summary>
        /// Gets the newest compatible stable version as normalized text.
        /// </summary>
        /// <param name="targetFrameworkMoniker">The package target framework scope.</param>
        /// <returns>The latest compatible stable version text, if one is known.</returns>
        public string? GetLatestCompatibleStableVersion(string? targetFrameworkMoniker)
        {
            return GetLatestCompatibleStableNuGetVersion(targetFrameworkMoniker)?.ToNormalizedString();
        }

        /// <summary>
        /// Determines whether a package version appears compatible with the supplied framework.
        /// </summary>
        /// <param name="version">The package version metadata.</param>
        /// <param name="targetFrameworkMoniker">The project target framework.</param>
        /// <returns><see langword="true"/> when compatibility can be inferred; otherwise, <see langword="false"/>.</returns>
        private static bool SupportsFramework(PackageRegistrationLeafInfo version, string? targetFrameworkMoniker)
        {
            if (version.SupportedFrameworks.Count == 0 || string.IsNullOrWhiteSpace(targetFrameworkMoniker))
            {
                return true;
            }

            NuGetFramework projectFramework = ParseFramework(targetFrameworkMoniker);
            if (projectFramework.IsUnsupported)
            {
                return true;
            }

            List<NuGetFramework> candidateFrameworks = version.SupportedFrameworks
                .Select(ParseFramework)
                .Where(static framework => !framework.IsUnsupported)
                .ToList();

            if (candidateFrameworks.Count == 0)
            {
                return true;
            }

            return candidateFrameworks.Any(candidateFramework =>
                DefaultCompatibilityProvider.Instance.IsCompatible(projectFramework, candidateFramework));
        }

        /// <summary>
        /// Determines whether a candidate version stays within a safe upgrade line when framework metadata is incomplete.
        /// </summary>
        /// <param name="version">The candidate package version metadata.</param>
        /// <param name="currentVersion">The currently used package version.</param>
        /// <returns><see langword="true"/> when the candidate is eligible for upgrade comparison; otherwise, <see langword="false"/>.</returns>
        private static bool SupportsUpgradePath(PackageRegistrationLeafInfo version, NuGetVersion? currentVersion)
        {
            if (currentVersion is null)
            {
                return true;
            }

            if (!NuGetVersion.TryParse(version.Version, out NuGetVersion? candidateVersion))
            {
                return false;
            }

            if (version.SupportedFrameworks.Count > 0)
            {
                return true;
            }

            return candidateVersion.Major == currentVersion.Major;
        }

        /// <summary>
        /// Parses a NuGet framework moniker using folder-name semantics.
        /// </summary>
        /// <param name="framework">The framework text to parse.</param>
        /// <returns>The parsed framework, or <see cref="NuGetFramework.UnsupportedFramework"/> when parsing fails.</returns>
        private static NuGetFramework ParseFramework(string framework)
        {
            try
            {
                return NuGetFramework.Parse(framework);
            }
            catch (ArgumentException)
            {
                try
                {
                    return NuGetFramework.ParseFolder(framework);
                }
                catch (ArgumentException)
                {
                    return NuGetFramework.UnsupportedFramework;
                }
            }
        }
    }

    /// <param name="ProjectUrl">Optional <c>projectUrl</c> from the NuGet catalog entry for this version.</param>
    /// <param name="ProjectUrlReachable">
    /// After enrichment, <see langword="true"/> when an http(s) HEAD/GET to <paramref name="ProjectUrl"/> succeeded;
    /// <see langword="false"/> when probed and failed; <see langword="null"/> when not probed or URL missing/invalid.
    /// </param>
    private sealed record PackageRegistrationLeafInfo(string Version, PackageDeprecationInfo? Deprecation, IReadOnlyList<PackageVulnerabilityRecord> Vulnerabilities, IReadOnlyList<string> SupportedFrameworks, bool IsListed, DateTimeOffset? PublishedUtc, string? ProjectUrl, bool? ProjectUrlReachable);

    private sealed record PackageDeprecationInfo(string? Message, IReadOnlyList<string> Reasons, string? AlternatePackageId, string? AlternatePackageRange);
}
