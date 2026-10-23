using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace PhotoCurator.Core.Server;

/// <summary>Server-sent events to the open window(s): "library" when the folder changed, "job" for copy/export progress.</summary>
sealed class EventHub
{
    readonly List<Channel<string>> _clients = new();
    readonly object _sync = new();

    public void Publish(string name, object payload)
    {
        var message = $"event: {name}\ndata: {JsonSerializer.Serialize(payload, CuratorServer.JsonOptions)}\n\n";
        lock (_sync)
        {
            foreach (var client in _clients) client.Writer.TryWrite(message);
        }
    }

    public async Task ServeAsync(HttpContext context)
    {
        var res = context.Response;
        res.Headers.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-store";
        res.Headers["X-Accel-Buffering"] = "no";

        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_sync) _clients.Add(channel);
        var cancel = context.RequestAborted;
        try
        {
            await res.WriteAsync(": connected\n\n", cancel);
            await res.Body.FlushAsync(cancel);
            while (!cancel.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
                string message;
                try
                {
                    message = await channel.Reader.ReadAsync(heartbeat.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    message = ": ping\n\n";
                }
                await res.Body.WriteAsync(Encoding.UTF8.GetBytes(message), cancel);
                await res.Body.FlushAsync(cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed or reloaded.
        }
        finally
        {
            lock (_sync) _clients.Remove(channel);
        }
    }
}
