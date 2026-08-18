using System.Globalization;

namespace TextCascade.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "user", StringComparison.Ordinal))
        {
            return Cli.RunCli(args);
        }

        if (args.Length > 0 && args[0] == "serve")
        {
            return ServerHost.RunServer(args.Skip(1).ToArray());
        }

        return ServerHost.RunServer(args);
    }
}
