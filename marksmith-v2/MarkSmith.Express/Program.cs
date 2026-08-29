using MarkSmith.Express;

// Marksmith Express entry point: a zero-dependency, cross-platform loopback web UI + REST API
// over the same MarkSmith.Core conversion pipeline the desktop app uses.
//
//   marksmith-express [--port <n>] [--no-browser]

int port = 5000;
bool openBrowser = true;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" or "-p" when i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed):
            port = parsed;
            i++;
            break;
        case "--no-browser":
            openBrowser = false;
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: marksmith-express [--port <n>] [--no-browser]");
            return 0;
    }
}

using var server = new ExpressServer();

try
{
    server.Start(port, openBrowser);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to start Marksmith Express: {ex.Message}");
    return 1;
}

using var stopped = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.Set();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopped.Set();

stopped.Wait();
Console.WriteLine("Marksmith Express stopped.");
return 0;
