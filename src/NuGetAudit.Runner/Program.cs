using NuGetAudit.Core;

MsBuildHostBootstrap.TryRegister();

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string solutionPath = Path.GetFullPath(args[0]);

if (!File.Exists(solutionPath))
{
    Console.Error.WriteLine($"Solution file not found: {solutionPath}");
    return 2;
}

AuditCommandOptions options = ParseOptions(args.Skip(1).ToArray(), solutionPath);
NuGetAuditRunner runner = new();
AuditRunResult result = await runner.RunAsync(options, CancellationToken.None);

Console.WriteLine($"Snapshot: {result.CurrentSnapshot.SnapshotId}");
Console.WriteLine($"Projects analysed: {result.CurrentSnapshot.Projects.Count}");
Console.WriteLine($"Packages discovered: {result.CurrentSnapshot.Projects.Sum(static project => project.Packages.Count)}");
Console.WriteLine(BuildComparisonSummary(result));
Console.WriteLine($"Markdown report: {result.ReportSet.MarkdownReportPath}");
Console.WriteLine($"Snapshot json: {result.ReportSet.SnapshotJsonPath}");
Console.WriteLine($"Knowledge json: {result.ReportSet.KnowledgeJsonPath}");
        Console.WriteLine($"Knowledge sqlite: {result.ReportSet.KnowledgeDatabasePath ?? "(not written)"}");

if (!string.IsNullOrWhiteSpace(result.ReportSet.DeltaJsonPath))
{
    Console.WriteLine($"Delta json: {result.ReportSet.DeltaJsonPath}");
}

return 0;

static AuditCommandOptions ParseOptions(string[] args, string solutionPath)
{
    string outputDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, ".nugetaudit");
    bool includeJsonCompanion = true;
    bool includeMarkdown = true;

    for (int index = 0; index < args.Length; index++)
    {
        string argument = args[index];

        if (string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
        {
            outputDirectory = Path.GetFullPath(args[++index]);
            continue;
        }

        if (string.Equals(argument, "--no-json", StringComparison.OrdinalIgnoreCase))
        {
            includeJsonCompanion = false;
            continue;
        }

        if (string.Equals(argument, "--no-markdown", StringComparison.OrdinalIgnoreCase))
        {
            includeMarkdown = false;
        }
    }

    return new AuditCommandOptions(solutionPath, outputDirectory)
    {
        WriteMarkdownReport = includeMarkdown,
        WriteJsonCompanion = includeJsonCompanion
    };
}

static void PrintUsage()
{
    Console.WriteLine("Usage: NuGetAudit.Runner <path-to-input.sln|.slnx|.csproj|.vbproj|.fsproj> [--output <directory>] [--no-json] [--no-markdown]");
}

static string BuildComparisonSummary(AuditRunResult result)
{
    if (!string.IsNullOrWhiteSpace(result.Delta.ComparisonNote))
    {
        return $"Comparison status: {result.Delta.ComparisonNote}";
    }

    if (result.PreviousSnapshot is null || string.IsNullOrWhiteSpace(result.Delta.PreviousSnapshotId))
    {
        return "Comparison status: No previous accepted snapshot was available for comparison.";
    }

    return $"Changes detected: {result.Delta.Changes.Count}";
}
