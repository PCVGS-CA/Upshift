using System.Text.Json.Serialization;

namespace Upshift.Core.Store;

/// <summary>store.json: the Upscaler Store's entries (in the repo next to catalog.v2.json, fetched from main like it).</summary>
public sealed class StoreFile
{
    public int Schema { get; set; } = 1;
    /// <summary>A timestamp; a downloaded store.json is only used when it's newer than the built-in one.</summary>
    public string Updated { get; set; } = "";
    public string? Notes { get; set; }
    public List<StoreEntry> Entries { get; set; } = new();
}

public sealed class StoreEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    /// <summary>The project's page, https://github.com/owner/repo for anything downloaded.</summary>
    public string Repo { get; set; } = "";
    /// <summary>The licence as the project states it, or "not stated".</summary>
    public string License { get; set; } = "not stated";
    /// <summary>One line.</summary>
    public string Description { get; set; } = "";
    /// <summary>Cards it works on; empty means any. A vendor with no generations means every card of that vendor.</summary>
    public List<StoreGpu> Gpus { get; set; } = new();
    /// <summary>"DX12", "Vulkan", "DX11"; empty means any.</summary>
    public List<string> Apis { get; set; } = new();
    /// <summary>Stable, Beta or Experimental.</summary>
    public string Status { get; set; } = "Experimental";
    /// <summary>recipe, author-setup, user-supplied, link-only or builtin.</summary>
    public string Type { get; set; } = "link-only";
    public string? Notes { get; set; }
    /// <summary>builtin: the app page it opens (helixsr, dlss5, testbuilds).</summary>
    public string? Builtin { get; set; }
    /// <summary>recipe: the steps.</summary>
    public List<RecipeStep>? Recipe { get; set; }
    /// <summary>author-setup: what to download and run.</summary>
    public AuthorSetupSpec? Setup { get; set; }
    /// <summary>user-supplied: the file the user picks and where it goes.</summary>
    public UserSuppliedSpec? UserFile { get; set; }

    [JsonIgnore] public bool Installable => Type is StoreTypes.Recipe or StoreTypes.AuthorSetup or StoreTypes.UserSupplied;
}

public sealed class StoreGpu
{
    /// <summary>nvidia, amd or intel.</summary>
    public string Vendor { get; set; } = "";
    /// <summary>As Upshift names them: "RDNA 4", "Blackwell", "Alchemist"… Empty = every card of the vendor.</summary>
    public List<string> Generations { get; set; } = new();
}

/// <summary>
/// One recipe step. The step list is fixed (<see cref="StoreTypes.Steps"/>); a store.json with any other step, or with a
/// path outside the allowed places, has that entry rejected when it's loaded.
/// </summary>
public sealed class RecipeStep
{
    public string Type { get; set; } = "";

    // download: the author's own GitHub release (the entry's repo). Asset = a regex for the file's name; Tag = "latest" or a tag.
    /// <summary>download/extract/copy: the download's name inside the recipe.</summary>
    public string? Id { get; set; }
    public string? Asset { get; set; }
    public string? Tag { get; set; }

    // extract: From = a download id.
    // copy: From = "downloadId:path inside" (a file or folder), To = a path under {GameFolder}, {OptiScalerFolder} or {UpshiftData}\<entry id>.
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>copy: files (relative to To) the user may edit; on a version change their edited copy is kept.</summary>
    public List<string>? Keep { get; set; }

    // rename: File = a path the recipe copied; Name = the new file name (may be {ProxyName}: the name OptiScaler is loaded as).
    public string? File { get; set; }
    public string? Name { get; set; }

    // ini-set: File = an ini under {GameFolder} or {UpshiftData}; Section, Key, Value (may use {GameFolder}, {UpshiftData}, {EntryFolder}).
    public string? Section { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }

    // requires: Values, all must hold ("OptiScaler from Upshift", "OptiScaler test build", "DX12", "Vulkan", "DX12|Vulkan",
    // "GPU:amd", "GPU:amd:RDNA 4|RDNA 3").
    public List<string>? Values { get; set; }

    // message: Text, shown after the install.
    public string? Text { get; set; }
}

public sealed class AuthorSetupSpec
{
    /// <summary>The release asset (regex) and tag ("latest" or a tag) from the entry's repo.</summary>
    public string Asset { get; set; } = "";
    public string Tag { get; set; } = "latest";
    /// <summary>The program to run, relative to the unpacked download (or the asset itself when it's an .exe).</summary>
    public string Run { get; set; } = "";
    /// <summary>Arguments; may use {GameFolder} and {EntryFolder}.</summary>
    public List<string> Args { get; set; } = new();
}

public sealed class UserSuppliedSpec
{
    /// <summary>The file's name; the picked file must have it.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Where it goes: a path under {GameFolder} or {OptiScalerFolder}.</summary>
    public string To { get; set; } = "";
    /// <summary>Where to get it (the project page by default).</summary>
    public string? Link { get; set; }
}

public static class StoreTypes
{
    public const string Recipe = "recipe", AuthorSetup = "author-setup", UserSupplied = "user-supplied", LinkOnly = "link-only", Builtin = "builtin";
    public static readonly string[] All = { Recipe, AuthorSetup, UserSupplied, LinkOnly, Builtin };
    public static readonly string[] Statuses = { "Stable", "Beta", "Experimental" };
    public static readonly string[] Steps = { "download", "extract", "copy", "rename", "ini-set", "requires", "message" };
    public static readonly string[] Builtins = { "helixsr", "dlss5", "testbuilds" };
    public static readonly string[] Apis = { "DX12", "Vulkan", "DX11" };

    public static string Label(string type) => type switch
    {
        Recipe => "Installs into a game",
        AuthorSetup => "Runs the author's setup",
        UserSupplied => "You supply the file",
        LinkOnly => "Link only",
        Builtin => "Built into Upshift",
        _ => type
    };
}
