namespace HyperVManage.Services;

/// <summary>A Microsoft download page for Windows 11 ISOs.</summary>
public sealed record IsoDownload(string Text, Uri Page);

/// <summary>
/// Where to get a Windows ISO: Microsoft's page for this PC's kind of processor first, since
/// Hyper-V only runs Windows built for it, then the other kind's, for an ISO to take elsewhere.
/// </summary>
public static class IsoDownloads
{
    public static IsoDownload X64 { get; } =
        new("Download Windows 11 for x64 PCs (Intel and AMD)", new Uri("https://www.microsoft.com/software-download/windows11"));

    public static IsoDownload Arm64 { get; } =
        new("Download Windows 11 for Arm64 PCs", new Uri("https://www.microsoft.com/software-download/windows11arm64"));

    /// <summary>The page for this PC first, labelled as such, then the other one.</summary>
    public static IReadOnlyList<IsoDownload> ForThisPcFirst(bool hostIsArm64)
    {
        var (mine, other) = hostIsArm64 ? (Arm64, X64) : (X64, Arm64);
        return [mine with { Text = mine.Text + ", which this PC needs" }, other];
    }

    /// <summary>Opens a page in the user's browser, as the user rather than as administrator.</summary>
    public static void Open(Uri page) => Browser.Open(page);
}
