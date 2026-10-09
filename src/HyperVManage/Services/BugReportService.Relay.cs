namespace HyperVManage.Services;

// Empty here, so a build from the repository sends reports through GitHub's issue form in the
// browser. The release workflow writes the relay's address and key into this file before it
// builds; see relay/README.md. Never commit real values.
public sealed partial class BugReportService
{
    private const string RelayUrl = "";
    private const string RelayKey = "";
}
