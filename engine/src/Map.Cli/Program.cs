using System.CommandLine;
using GateKit.Site;

// The site's commands are the whole of this tool: `map build`, `map serve`, `map check`, ...
var site = SiteCommand.CreateCommand();
var root = new RootCommand(site.Description ?? "the GateKit map site");
foreach (var option in site.Options)
{
    root.Options.Add(option);
}
foreach (var command in site.Subcommands)
{
    root.Subcommands.Add(command);
}
return root.Parse(args).Invoke();
