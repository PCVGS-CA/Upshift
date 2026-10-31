namespace Upshift.Core.Install;

/// <summary>
/// OptiPatcher (github.com/optiscaler/OptiPatcher, MIT): an ASI plugin for OptiScaler that exposes DLSS / DLSS-FG inputs in
/// supported games without spoofing (its README: for AMD and Intel users; it avoids spoofing's overhead and crashes).
/// Per its README and OptiScaler 0.9.4's source, it goes into the "plugins" folder under OptiScaler's DLL folder, with
/// <c>[Plugins] LoadAsiPlugins=true</c>: OptiDllPath defaults to the exe's folder in 0.9.x (".\") and to ".\OptiScaler" in
/// the v10 test builds, and the plugin path defaults to that folder's "plugins". Added and removed through Configure, so
/// the file and the setting are in the game's record: switching it off, or uninstalling, takes both back.
/// </summary>
public static class OptiPatcher
{
    public const string ComponentId = "optipatcher";
    public const string FileName = "OptiPatcher.asi";
    public const string GameSupportUrl = "https://github.com/optiscaler/OptiPatcher/blob/main/GameSupport.md";

    /// <summary>Where it goes, relative to OptiScaler's folder (the exe folder): OptiScaler's DLL folder + "plugins".</summary>
    public static string RelativePath(string targetDir)
    {
        // The v10 layout keeps OptiScaler's DLLs in an "OptiScaler" folder, which is then its default DLL folder.
        var v10 = Path.Combine(targetDir, "OptiScaler");
        var dllFolder = Directory.Exists(v10) && Directory.EnumerateFiles(v10, "*.dll").Any() ? "OptiScaler" : null;
        return dllFolder is null ? Path.Combine("plugins", FileName) : Path.Combine(dllFolder, "plugins", FileName);
    }

    /// <summary>The ini value it needs; null puts back what was there before Upshift's change.</summary>
    public static List<IniSetting> Settings(bool on) => new() { new IniSetting("Plugins", "LoadAsiPlugins", on ? "true" : null) };

    /// <summary>Its path in the game's record, or null when Upshift didn't add it.</summary>
    public static string? InstalledPath(InstallManifest manifest) =>
        manifest.Added.Select(f => f.Path).FirstOrDefault(p => Path.GetFileName(p).Equals(FileName, StringComparison.OrdinalIgnoreCase));
}
