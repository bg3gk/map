namespace GateKit.Site;

/// <summary>
/// Where the map keeps what it reads and what it renders, when nothing says otherwise: inside the
/// checkout of <c>bg3gk/map</c> the program runs from - <c>data/</c> for the databases
/// <c>map fetch</c> downloads, <c>site/</c> for what <c>map build</c> renders, and the component map
/// in the <c>gatekit</c> submodule. <c>MAP_DATA</c> moves the data elsewhere.
/// </summary>
public static class MapPaths
{
    /// <summary>The checkout: the nearest folder above the program, or above the current folder, that holds <c>Map.slnx</c>.</summary>
    public static string Root
    {
        get
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (var folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
                {
                    if (File.Exists(Path.Combine(folder.FullName, "Map.slnx")))
                    {
                        return folder.FullName;
                    }
                }
            }
            return Directory.GetCurrentDirectory();
        }
    }

    /// <summary>The folder the databases are in.</summary>
    public static string Data =>
        Environment.GetEnvironmentVariable("MAP_DATA") is { Length: > 0 } data ? data : Path.Combine(Root, "data");

    public static string IndexDatabase => Path.Combine(Data, "gameindex.sqlite");

    public static string SymbolDatabase => Path.Combine(Data, "symbols.sqlite");

    /// <summary>The site folder <c>map build</c> renders into and <c>map serve</c> serves.</summary>
    public static string Site => Path.Combine(Root, "site");

    /// <summary>Our component map: <c>research/coverage</c> of the gatekit submodule.</summary>
    public static string? Coverage =>
        ComponentMap.Find(Path.Combine(Root, "gatekit")) ?? ComponentMap.Find(AppContext.BaseDirectory);
}
