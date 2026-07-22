using Inutil.TestKit;
using Inutil.TestKit.Cli;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: inutil-testkit <aggregate <file> | selftest>");
    return 2;
}

switch (args[0])
{
    case "aggregate":
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: inutil-testkit aggregate <sidecar.jsonl>");
            return 2;
        }
        var v = Aggregator.Judge(args[1]);
        Console.Write(v.Render());
        return v.Ok ? 0 : 1;

    case "selftest":
        return SelfTest.Run();

    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}
