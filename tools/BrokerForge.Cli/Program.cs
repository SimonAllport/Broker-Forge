using BrokerForge.Cli;

return args.FirstOrDefault() switch
{
    "validate" => ValidateCommand.Run(args[1..], Console.Out),
    "draft" => DraftCommand.Run(args[1..], Console.Out),
    "generate" => await GenerateCommand.RunAsync(args[1..], Console.Out, CancellationToken.None),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          bf validate <descriptorsRoot> --schemas <schemasRoot>
          bf draft <openapi> --instance <name> --prefix <p> --out <dir> [--answers <json>] [--root <dir>]
          bf generate --org <url> --schemas <dir> --descriptors <dir> --instance <name> --solution <unique> --publisher <unique> --host-url <url> --scope <scope> [--dry-run] [--confirm <fingerprint>]
        """);
    return 2;
}
