# NuGetAudit

A multi-surface NuGet package auditing system for .NET solutions. Answers: *"What NuGet packages are in this codebase, what did we know about them, and which need attention now?"*

Surfaces the same analysis engine from three delivery surfaces: a command-line runner, a standalone WPF workbench, and a Visual Studio extension (VSIX).

**Source last updated:** 2026-04-04

**Initiated:** 2025-06-24 · **Framework:** .NET 10 · **Solution:** `NuGetAudit.slnx`

---

## What It Does

1. **Discovers** projects from `.sln` / `.slnx` files
2. **Analyses** package declarations + resolved graph from `project.assets.json`
3. **Enriches** with live health metadata from nuget.org (vulnerabilities, deprecations)
4. **Calculates** a criticality score per package using weighted factors
5. **Persists** immutable timestamped snapshots (JSON + Markdown) and a knowledge timeline (SQLite)
6. **Compares** against the last accepted snapshot to produce a delta
7. **Presents** results in a WPF workbench and inside Visual Studio

---

## Solution Structure

| Project | Responsibility |
|---------|---------------|
| `NuGetAudit.Core` | Audit engine, snapshots, delta, knowledge, persistence |
| `NuGetAudit.Intelligence` | Criticality scoring, outbound reporting contracts |
| `NuGetAudit.Presentation` | Shared WPF controls (explorer, history, dependency graph) |
| `NuGetAudit.Runner` | CLI entry point |
| `NuGetAudit.Workbench` | Standalone WPF desktop workbench |
| `NuGetAudit.VisualStudioHost` | VSIX package, tool window, Error List integration |
| `NuGetAudit.DbUtilities` | Knowledge timeline DB schema and maintenance |

---

## Getting Started

**CLI:** `dotnet run --project src/NuGetAudit.Runner -- --solution "path\to\solution.sln"`

**Workbench:** Open `NuGetAudit.slnx` in Visual Studio, start `NuGetAudit.Workbench`.

**VS Extension:** Build and deploy `NuGetAudit.VisualStudioHost` VSIX.

## Requirements

- .NET 10.0, .NET Framework 4.72, netstandard2.0

