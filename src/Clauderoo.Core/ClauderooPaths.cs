namespace Clauderoo.Core;

public record ClauderooPaths(string UserProfile)
{
    public string Root => Path.Combine(UserProfile, ".clauderoo");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string ProfilesDir => Path.Combine(Root, "profiles");
    public string RemovedDir => Path.Combine(ProfilesDir, "_removed");
    public string DefaultHomeDir => Path.Combine(UserProfile, ".claude");
    public string DefaultHomeJson => Path.Combine(UserProfile, ".claude.json");

    public string ProfileDir(string name) => Path.Combine(ProfilesDir, name);

    public static ClauderooPaths ForCurrentUser() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
