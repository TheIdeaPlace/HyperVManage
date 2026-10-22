namespace HyperVManage.Services;

/// <summary>A download page for an installer ISO.</summary>
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

    /// <summary>The Ubuntu release New Ubuntu Virtual Machine installs; New-HyperVRdpVM.ps1 has the same.</summary>
    public const string UbuntuSeries = "26.04";

    /// <summary>
    /// Ubuntu's own folder for the release this PC's processor needs, which New Ubuntu Virtual
    /// Machine downloads from: Ubuntu keeps Arm64 releases on cdimage and x64 ones on releases.
    /// An ISO saved from it to Downloads is used by the build rather than downloaded again.
    /// </summary>
    public static IsoDownload UbuntuForThisPc(bool hostIsArm64) => hostIsArm64
        ? new($"Download Ubuntu {UbuntuSeries} LTS for Arm64 PCs, which this PC needs",
            new Uri($"https://cdimage.ubuntu.com/ubuntu/releases/{UbuntuSeries}/release/"))
        : new($"Download Ubuntu {UbuntuSeries} LTS for x64 PCs (Intel and AMD), which this PC needs",
            new Uri($"https://releases.ubuntu.com/{UbuntuSeries}/"));

    /// <summary>Opens a page in the user's browser, as the user rather than as administrator.</summary>
    public static void Open(Uri page) => Browser.Open(page);
}
