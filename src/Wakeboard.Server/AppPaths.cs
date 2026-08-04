namespace Wakeboard;

public sealed class AppPaths
{
    public string Root { get; }
    public string BinDirectory => Path.Combine(Root, "bin");
    public string DataDirectory => Path.Combine(Root, "data");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public string InstalledExecutable => Path.Combine(BinDirectory, "Wakeboard.exe");

    public AppPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("WAKEBOARD_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Wakeboard");
    }
}
