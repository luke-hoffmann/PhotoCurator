using PhotoCurator.Core.Server;

// Runs the real server and UI on any OS, for development and the browser tests. There is no native folder picker here,
// so the UI asks for typed paths instead.
// Usage: dotnet run --project tools/DevHost -- [--port 8790] [--data <folder>] [--token <token>] [--open <photo folder>]
var port = 8790;
string? data = null;
string? token = null;
string? open = null;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[i + 1]); break;
        case "--data": data = Path.GetFullPath(args[i + 1]); break;
        case "--token": token = args[i + 1]; break;
        case "--open": open = Path.GetFullPath(args[i + 1]); break;
    }
}
data ??= Path.Combine(Path.GetTempPath(), "PhotoCurator-dev-" + Guid.NewGuid().ToString("N")[..8]);

await using var server = new CuratorServer(data, host: null, token: token);
await server.StartAsync(port);
if (open is not null) server.OpenFolder(open);
Console.WriteLine($"PhotoCurator dev host: {server.Url}");
Console.WriteLine($"Data: {data}");
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
