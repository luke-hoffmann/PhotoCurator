namespace PhotoCurator.Core.Server;

/// <summary>Progress of a long task (copying a folder, exporting for web), as the UI sees it.</summary>
public sealed class JobStatus
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public long Done { get; set; }
    public long Total { get; set; }
    public string Detail { get; set; } = "";
    /// <summary>"running", "done", "failed" or "cancelled".</summary>
    public string State { get; set; } = "running";
    public string? Error { get; set; }
    public object? Result { get; set; }
}

/// <summary>Runs one long task at a time in the background and reports its progress through the event hub.</summary>
sealed class JobRunner
{
    readonly EventHub _events;
    readonly object _sync = new();
    JobStatus? _current;
    CancellationTokenSource? _cancel;
    long _lastPublish;

    public JobRunner(EventHub events) => _events = events;

    public JobStatus? Current { get { lock (_sync) return _current; } }

    public JobStatus Start(string kind, string title, Func<Action<long, long, string>, CancellationToken, object?> work)
    {
        JobStatus job;
        CancellationToken token;
        lock (_sync)
        {
            if (_current is { State: "running" }) throw new CuratorException("Wait for the current copy or export to finish.", 409);
            job = new JobStatus { Id = Guid.NewGuid().ToString("N")[..12], Kind = kind, Title = title };
            _current = job;
            _cancel = new CancellationTokenSource();
            token = _cancel.Token;
        }
        Publish(job, force: true);

        _ = Task.Run(() =>
        {
            try
            {
                job.Result = work((done, total, detail) =>
                {
                    job.Done = done;
                    job.Total = total;
                    job.Detail = detail;
                    Publish(job, force: false);
                }, token);
                job.State = "done";
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
            }
            catch (CuratorException e)
            {
                job.State = "failed";
                job.Error = e.Message;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                job.State = "failed";
                job.Error = e.Message;
            }
            Publish(job, force: true);
        });
        return job;
    }

    public void Cancel()
    {
        lock (_sync) _cancel?.Cancel();
    }

    void Publish(JobStatus job, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastPublish) < 100) return;
        Interlocked.Exchange(ref _lastPublish, now);
        _events.Publish("job", job);
    }
}
