using System.Reflection;

namespace HyperVManage.Services;

/// <summary>Where Hyper-V Manage lives, and which version this is.</summary>
public static class AppInfo
{
    public const string RepoUrl = "https://github.com/kellylford/HyperVManage";
    public const string ReleasesApiUrl = "https://api.github.com/repos/kellylford/HyperVManage/releases?per_page=20";

    /// <summary>The release page for a version, which says what's new in it.</summary>
    public static Uri ReleasePage(string version) => new($"{RepoUrl}/releases/tag/v{version}");

    /// <summary>The build's version, from the project or the release tag; .NET adds "+commit"
    /// to the informational version, which isn't for people.</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];
}
