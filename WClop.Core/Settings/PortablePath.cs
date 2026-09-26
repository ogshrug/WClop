namespace WClop.Core.Settings;

/// <summary>
/// Stores paths relative to <c>%USERPROFILE%</c> so settings survive a different username
/// or a synced settings file (the Windows version of Clop storing paths as <c>~/…</c>).
/// </summary>
public static class PortablePath
{
    private const string Token = "%USERPROFILE%";

    public static string Contract(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        if (profile.Length == 0)
            return path;

        if (path.Equals(profile, StringComparison.OrdinalIgnoreCase))
            return Token;

        if (path.Length > profile.Length
            && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            && path[profile.Length] is '\\' or '/')
            return Token + path[profile.Length..];

        return path;
    }

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path);
}
