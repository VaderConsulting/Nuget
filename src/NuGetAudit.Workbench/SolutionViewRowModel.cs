using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NuGetAudit.Workbench;

/// <summary>
/// Mutable solution row for the catalog grid so background audits can update one row without rebuilding the whole collection.
/// </summary>
internal sealed class SolutionViewRowModel : INotifyPropertyChanged
{
    private string _AuditStatus = string.Empty;
    private string _SolutionName = string.Empty;
    private string _SolutionPath = string.Empty;
    private int _ProjectCount;
    private int _PackageCount;
    private string _AnalysisStatus = string.Empty;
    private DateTimeOffset _CapturedUtc;

    public SolutionViewRowModel(string catalogInputPath)
    {
        CatalogInputPath = catalogInputPath;
    }

    /// <summary>
    /// The catalog entry input path this row represents (stable key for lookups).
    /// </summary>
    public string CatalogInputPath { get; }

    public string AuditStatus
    {
        get => _AuditStatus;
        set => SetField(ref _AuditStatus, value);
    }

    public string SolutionName
    {
        get => _SolutionName;
        set => SetField(ref _SolutionName, value);
    }

    public string SolutionPath
    {
        get => _SolutionPath;
        set => SetField(ref _SolutionPath, value);
    }

    public int ProjectCount
    {
        get => _ProjectCount;
        set => SetField(ref _ProjectCount, value);
    }

    public int PackageCount
    {
        get => _PackageCount;
        set => SetField(ref _PackageCount, value);
    }

    public string AnalysisStatus
    {
        get => _AnalysisStatus;
        set => SetField(ref _AnalysisStatus, value);
    }

    public DateTimeOffset CapturedUtc
    {
        get => _CapturedUtc;
        set => SetField(ref _CapturedUtc, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Populates or refreshes display fields from the current audit status and optional snapshot data.
    /// </summary>
    public void RefreshDisplay(string auditStatus, AuditCatalogEntry entry, CatalogSnapshotData? snapshotOrNull)
    {
        AuditStatus = auditStatus;
        if (snapshotOrNull is null)
        {
            SolutionName = entry.DisplayName;
            SolutionPath = entry.InputPath;
            ProjectCount = 0;
            PackageCount = 0;
            AnalysisStatus = "No snapshot";
            CapturedUtc = entry.AddedUtc;
            return;
        }

        SolutionName = snapshotOrNull.SolutionName;
        SolutionPath = snapshotOrNull.SolutionPath;
        ProjectCount = snapshotOrNull.ProjectCount;
        PackageCount = snapshotOrNull.PackageCount;
        AnalysisStatus = snapshotOrNull.AnalysisStatus;
        CapturedUtc = snapshotOrNull.CapturedUtc;
    }

    public static SolutionViewRowModel FromEntry(AuditCatalogEntry entry, string auditStatus, CatalogSnapshotData? snapshotOrNull)
    {
        SolutionViewRowModel Model = new(entry.InputPath);
        Model.RefreshDisplay(auditStatus, entry, snapshotOrNull);
        return Model;
    }

    private void SetField<T>(ref T Field, T Value, [CallerMemberName] string? PropertyName = null)
    {
        if (Equals(Field, Value))
        {
            return;
        }

        Field = Value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
    }
}
