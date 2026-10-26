namespace FontStudio.Models;

public sealed record DuplicateFontRow(
    string Group,
    string MatchType,
    string Family,
    string Style,
    string Version,
    string Path,
    long SizeBytes,
    string Sha256)
{
    public string SizeText => $"{SizeBytes / 1024d:N1} KB";
    public string HashShort => Sha256.Length >= 12 ? Sha256[..12] : Sha256;
}

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateFontRow> Rows,
    int FilesScanned,
    int ExactGroups,
    int FaceGroups,
    int InvalidFiles);
