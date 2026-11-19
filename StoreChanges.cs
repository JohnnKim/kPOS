using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kpos;

internal static class StoreChanges
{
    internal static Task Map(WebApplication app, string database)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribers = new ConcurrentDictionary<Guid, Channel<long>>();
        CancellationToken stopping = app.Lifetime.ApplicationStopping;
        app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
        {
            long previous = -1; int heartbeat = 0;
            try
            {
                using var data = RegisterData.OpenHomeConnection(database);
                while(!stopping.IsCancellationRequested)
                {
                    long revision;
                    try { revision=data.Revision(); } catch(Microsoft.Data.Sqlite.SqliteException){await Task.Delay(500,stopping).ConfigureAwait(false);continue;}
                    if(revision != previous || ++heartbeat >= 20)
                    { foreach(var channel in subscribers.Values) channel.Writer.TryWrite(revision); previous = revision; heartbeat = 0; }
                    await Task.Delay(250, stopping).ConfigureAwait(false);
                }
            }
            catch(OperationCanceledException) when(stopping.IsCancellationRequested) { }
            finally { foreach(var channel in subscribers.Values) channel.Writer.TryComplete(); completed.TrySetResult(); }
        }));
        app.MapGet("/api/v1/changes", async (HttpContext context) =>
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stopping, context.RequestAborted);
            var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
            Guid id = Guid.NewGuid(); subscribers[id] = channel;
            context.Response.ContentType = "text/event-stream";
            try
            {
                using(var data = RegisterData.OpenHomeConnection(database)) channel.Writer.TryWrite(data.Revision());
                await foreach(long revision in channel.Reader.ReadAllAsync(cancelled.Token))
                {
                    await context.Response.WriteAsync($"data: {revision}\n\n", cancelled.Token);
                    await context.Response.Body.FlushAsync(cancelled.Token);
                }
            }
            catch(OperationCanceledException) when(cancelled.IsCancellationRequested) { }
            finally { subscribers.TryRemove(id, out _); channel.Writer.TryComplete(); }
        });
        return completed.Task;
    }
}

internal sealed partial class HomeTerminalClient
{
    internal async Task WatchChangesAsync(Action<long> changed, CancellationToken token)
    {
        long previous = -1;
        while(!token.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/changes");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false));
                while(await reader.ReadLineAsync(token).ConfigureAwait(false) is string line)
                    if(line.StartsWith("data: ", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6), out long revision) && revision != previous)
                    { previous = revision; changed(revision); }
            }
            catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException)
            { if(token.IsCancellationRequested) return; }
            try { await Task.Delay(1000, token).ConfigureAwait(false); } catch(OperationCanceledException) { return; }
        }
    }
}

internal sealed class LocalDataWatch : IDisposable
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 400 };
    internal LocalDataWatch(Form form, RegisterData data, Action changed)
    {
        long revision = data.Revision();
        timer.Tick += (_, _) =>
        {
            if(form.IsDisposed || !form.Visible) return;
            long current; try { current=data.Revision(); } catch(Microsoft.Data.Sqlite.SqliteException) { return; }
            if(current != revision) { revision = current; changed(); }
        };
        form.Shown += (_, _) => timer.Start(); form.Disposed += (_, _) => Dispose();
    }
    public void Dispose() => timer.Dispose();
}

internal static class LiveUi
{
    internal static void PostIfOpen(this Control control, Action action)
    {
        if(control.IsDisposed || !control.IsHandleCreated) return;
        try { control.BeginInvoke(() => { if(!control.IsDisposed) action(); }); }
        catch(InvalidOperationException) when(control.IsDisposed || !control.IsHandleCreated) { }
    }
}
