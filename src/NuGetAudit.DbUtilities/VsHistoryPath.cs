namespace NuGetAudit.DbUtilities;

/// <summary>
/// Path checks for Visual Studio local file history (<c>.vshistory</c>, any segment casing).
/// </summary>
public static class VsHistoryPath
{
    private const string VisualStudioLocalHistoryDirectoryName = ".vshistory";

    /// <summary>
    /// Returns <see langword="true"/> when any path segment equals <c>.vshistory</c> (ordinal case-insensitive).
    /// </summary>
    public static bool IsUnderVsHistoryFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string Normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        int SegmentStart = 0;
        for (int Index = 0; Index <= Normalized.Length; Index++)
        {
            if (Index == Normalized.Length || Normalized[Index] == Path.DirectorySeparatorChar)
            {
                int SegmentLength = Index - SegmentStart;
                if (SegmentLength > 0
                    && SegmentLength == VisualStudioLocalHistoryDirectoryName.Length
                    && string.Compare(
                        Normalized,
                        SegmentStart,
                        VisualStudioLocalHistoryDirectoryName,
                        0,
                        SegmentLength,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }

                SegmentStart = Index + 1;
            }
        }

        return false;
    }
}
