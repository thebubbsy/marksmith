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
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --port, -p <n>    Port for loopback server (default: 5000)");
            Console.WriteLine("  --no-browser      Do not automatically open web UI in browser");
            Console.WriteLine("  --help, -h        Show this help message");
            Console.WriteLine();
            Console.WriteLine("Supported formats: docx, html, pptx, epub.");
            Console.WriteLine("Note: PDF export requires the Desktop app (WebView2 Chromium engine).");
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
