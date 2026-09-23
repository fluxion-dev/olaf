using System.CommandLine;

var inputOption = new Option<string>("--input")
{
    Description = "Input file or directory to scan",
};
var formatOption = new Option<string>("--format")
{
    Description = "Output format: json|yaml|xml|html",
    DefaultValueFactory = _ => "json",
};
formatOption.AcceptOnlyFromAmong("json", "yaml", "xml", "html");
var outOption = new Option<string?>("--out")
{
    Description = "Output file path (default: stdout)",
};
var forceOption = new Option<bool>("--force")
{
    Description = "Overwrite output file if it exists",
};
var strictOption = new Option<bool>("--strict")
{
    Description = "Fail on unresolved or unknown licenses",
};
var ecosystemOption = new Option<string?>("--ecosystem")
{
    Description = "Limit scan to ecosystem: npm|nuget|pip",
};
ecosystemOption.AcceptOnlyFromAmong("npm", "nuget", "pip");
var verboseOption = new Option<bool>("--verbose")
{
    Description = "Enable verbose logging",
};

var rootCommand = new RootCommand("olaf license scanner (scaffold, no logic yet)")
{
    inputOption,
    formatOption,
    outOption,
    forceOption,
    strictOption,
    ecosystemOption,
    verboseOption,
};

rootCommand.SetAction(_ => 0);

return rootCommand.Parse(args).Invoke();
