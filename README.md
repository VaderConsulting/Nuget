# NuGetAudit

A multi-surface NuGet package auditing system for .NET solutions. Answers: what NuGet packages are in this codebase, what is known about them, and which need attention now. The same analysis engine ships as a command-line runner, a standalone WPF workbench, and a Visual Studio extension (VSIX).

**Source last updated:** 2026-04-04 · **Language:** C# · **Target:** .NET 10 / .NET Framework 4.7.2 / netstandard2.0 · **Output:** CLI, WPF workbench, VSIX · **Solution:** `NuGetAudit.slnx`

## Solution structure

| Project | Language | Type | Purpose |
|---------|----------|------|---------|
| `NuGetAudit.Core` | C# | Library | Audit engine, package state records, delta, knowledge, persistence |
| `NuGetAudit.Intelligence` | C# | Library | Criticality scoring and outbound reporting contracts |
| `NuGetAudit.Presentation` | C# | Library | Shared WPF controls (explorer, history, dependency graph) |
| `NuGetAudit.Runner` | C# | CLI | Command-line entry point |
| `NuGetAudit.Workbench` | C# | WPF exe | Standalone desktop workbench |
| `NuGetAudit.VisualStudioHost` | C# | VSIX | Tool window and Error List integration |
| `NuGetAudit.DbUtilities` | C# | Utility | Knowledge timeline DB schema and maintenance |

## How to open

Open `NuGetAudit.slnx` in Visual Studio 2022 or 2026.

- CLI: `dotnet run --project src/NuGetAudit.Runner -- --solution "path\to\solution.sln"`
- Workbench: start `NuGetAudit.Workbench`
- VS Extension: build and deploy `NuGetAudit.VisualStudioHost` VSIX

## Requirements

- Visual Studio 2022 or 2026
- .NET 10.0, .NET Framework 4.7.2, netstandard2.0

## Attribution and provenance

my working copy from Development folder `Nuget`.

## License

MIT © 2026 VaderConsulting for Dave Robinson's code. See `LICENSE`.
