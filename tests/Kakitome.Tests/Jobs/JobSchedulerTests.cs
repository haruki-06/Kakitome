using Microsoft.Extensions.DependencyInjection;
using Kakitome.Application.Jobs;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Jobs;

public sealed class JobSchedulerTests
{
    [Fact]
    public async Task Job_runs_to_success_and_progress_is_persisted()
    {
        var handler = new ScriptedHandler("t.light", JobResourceClass.Light, async (ctx, ct) =>
        {
            await ctx.ReportProgressAsync(0.5, "half");
            await ctx.SetEngineAsync("test-engine");
        });
        await using var f = await CreateAsync(handler);

        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.light"));
        await RunUntilSettledAsync(f);

        var done = await f.Jobs.GetAsync(job.Id);
        Assert.Equal(JobState.Succeeded, done!.State);
        Assert.Equal(1, done.Progress);
        Assert.Equal("test-engine", done.Engine);
        Assert.Equal(1, handler.Runs);
    }

    [Fact]
    public async Task A_queued_job_of_a_removed_stage_is_skipped_and_the_next_stage_still_runs()
    {
        var next = new ScriptedHandler("t.next", JobResourceClass.Light, (_, _) => Task.CompletedTask);
        await using var f = await CreateAsync(next);
        var retired = new JobRecord { Id = Guid.NewGuid(), Kind = "diarize", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await f.Jobs.AddAsync(retired); // left by an older version
        var dependent = await f.Scheduler.EnqueueAsync(new JobRequest("t.next") { DependsOn = retired.Id });

        await RunUntilSettledAsync(f);

        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(retired.Id))!.State);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(dependent.Id))!.State);
        Assert.Equal(1, next.Runs);
    }

    [Fact]
    public async Task Dependencies_run_in_order()
    {
        var order = new List<string>();
        var a = new ScriptedHandler("t.a", JobResourceClass.Light, (_, _) => { lock (order) { order.Add("a"); } return Task.CompletedTask; });
        var b = new ScriptedHandler("t.b", JobResourceClass.Light, (_, _) => { lock (order) { order.Add("b"); } return Task.CompletedTask; });
        await using var f = await CreateAsync(a, b);

        // Enqueue the dependent first so ordering cannot come from creation time.
        var first = await f.Scheduler.EnqueueAsync(new JobRequest("t.a") { Priority = -1 });
        var second = await f.Scheduler.EnqueueAsync(new JobRequest("t.b") { DependsOn = first.Id, Priority = 10 });
        Assert.Equal(JobWaitReason.Dependency, second.WaitReason);

        await RunUntilSettledAsync(f);

        Assert.Equal(["a", "b"], order);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(second.Id))!.State);
    }

    [Fact]
    public async Task Transient_failures_retry_with_backoff_then_fail_with_a_readable_error()
    {
        var handler = new ScriptedHandler("t.flaky", JobResourceClass.Light, (_, _) => throw new TransientJobException("file locked"));
        await using var f = await CreateAsync(handler);
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.flaky") { MaxAttempts = 3 });

        await RunUntilSettledAsync(f);
        var afterFirst = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Pending, afterFirst.State);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Equal(JobWaitReason.RetryBackoff, afterFirst.WaitReason);
        Assert.Equal("file locked", afterFirst.LastError);

        // Not retried before the backoff elapses.
        await RunUntilSettledAsync(f);
        Assert.Equal(1, handler.Runs);

        f.Time.Advance(JobScheduler.Backoff(1) + TimeSpan.FromSeconds(1));
        await RunUntilSettledAsync(f);
        f.Time.Advance(JobScheduler.Backoff(2) + TimeSpan.FromSeconds(1));
        await RunUntilSettledAsync(f);

        var final = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Failed, final.State);
        Assert.Equal(3, final.Attempts);
        Assert.Equal(3, handler.Runs);
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        var handler = new ScriptedHandler("t.bad", JobResourceClass.Light, (_, _) => throw new PermanentJobException("corrupt input"));
        await using var f = await CreateAsync(handler);
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.bad"));

        await RunUntilSettledAsync(f);

        var failed = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal("corrupt input", failed.LastError);
        Assert.Equal(1, handler.Runs);
    }

    [Fact]
    public async Task Cancel_stops_a_running_job_cascades_and_retry_restores_the_chain()
    {
        var gate = new TaskCompletionSource();
        var slow = new ScriptedHandler("t.slow", JobResourceClass.Light, async (_, ct) => await gate.Task.WaitAsync(ct));
        var next = new ScriptedHandler("t.next", JobResourceClass.Light, (_, _) => Task.CompletedTask);
        await using var f = await CreateAsync(slow, next);
        var first = await f.Scheduler.EnqueueAsync(new JobRequest("t.slow"));
        var second = await f.Scheduler.EnqueueAsync(new JobRequest("t.next") { DependsOn = first.Id });

        await f.Scheduler.TickAsync();
        await WaitForStateAsync(f, first.Id, JobState.Running);
        await f.Scheduler.CancelAsync(first.Id);

        Assert.Equal(JobState.Cancelled, (await f.Jobs.GetAsync(first.Id))!.State);
        Assert.Equal(JobState.Cancelled, (await f.Jobs.GetAsync(second.Id))!.State);

        gate.SetResult();
        await f.Scheduler.RetryAsync(first.Id);
        await RunUntilSettledAsync(f);

        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(first.Id))!.State);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(second.Id))!.State);
    }

    [Fact]
    public async Task Pause_keeps_the_checkpoint_and_resume_continues_from_it()
    {
        var seen = new List<string?>();
        var gate = new TaskCompletionSource();
        var handler = new ScriptedHandler("t.resumable", JobResourceClass.Light, async (ctx, ct) =>
        {
            lock (seen)
            {
                seen.Add(ctx.Job.Checkpoint);
            }

            if (ctx.Job.Checkpoint is null)
            {
                await ctx.SaveCheckpointAsync("step-1");
                await gate.Task.WaitAsync(ct);
            }
        });
        await using var f = await CreateAsync(handler);
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.resumable"));

        await f.Scheduler.TickAsync();
        await WaitUntilAsync(() => seen.Count == 1 && f.Jobs.GetAsync(job.Id).Result!.Checkpoint == "step-1");
        await f.Scheduler.PauseAsync(job.Id);
        Assert.Equal(JobState.Paused, (await f.Jobs.GetAsync(job.Id))!.State);

        await RunUntilSettledAsync(f);
        Assert.Single(seen); // paused jobs are not started

        await f.Scheduler.ResumeAsync(job.Id);
        await RunUntilSettledAsync(f);

        Assert.Equal([null, "step-1"], seen);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
    }

    [Fact]
    public async Task Heavy_work_waits_for_AC_power_in_auto_mode()
    {
        var heavy = new ScriptedHandler("t.heavy", JobResourceClass.Heavy, (_, _) => Task.CompletedTask);
        await using var f = await CreateAsync(heavy);
        f.SystemResources.Update(r => r with { OnAcPower = false });
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.heavy"));

        await RunUntilSettledAsync(f);
        var waiting = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Pending, waiting.State);
        Assert.Equal(JobWaitReason.OnBattery, waiting.WaitReason);

        f.SystemResources.Update(r => r with { OnAcPower = true });
        await RunUntilSettledAsync(f);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
    }

    [Fact]
    public async Task Always_process_mode_runs_heavy_work_on_battery()
    {
        var heavy = new ScriptedHandler("t.heavy", JobResourceClass.Heavy, (_, _) => Task.CompletedTask);
        await using var f = await CreateAsync(heavy);
        await f.Settings.UpdateAsync(s => s.Processing.Mode = ProcessingMode.AlwaysProcess);
        f.SystemResources.Update(r => r with { OnAcPower = false, BatteryPercent = 60 });
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.heavy"));

        await RunUntilSettledAsync(f);

        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
    }

    [Fact]
    public async Task Unplugging_power_preempts_a_running_heavy_job_without_counting_an_attempt()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var heavy = new ScriptedHandler("t.heavy", JobResourceClass.Heavy, async (ctx, ct) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                await ctx.SaveCheckpointAsync("50%");
                await gate.Task.WaitAsync(ct);
            }
        });
        await using var f = await CreateAsync(heavy);
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.heavy"));
        await f.Scheduler.TickAsync();
        await WaitForStateAsync(f, job.Id, JobState.Running);
        await WaitUntilAsync(() => f.Jobs.GetAsync(job.Id).Result!.Checkpoint == "50%");

        f.SystemResources.Update(r => r with { OnAcPower = false });
        await RunUntilSettledAsync(f);

        var deferred = (await f.Jobs.GetAsync(job.Id))!;
        Assert.Equal(JobState.Pending, deferred.State);
        Assert.Equal(JobWaitReason.OnBattery, deferred.WaitReason);
        Assert.Equal(0, deferred.Attempts);
        Assert.Equal("50%", deferred.Checkpoint);

        f.SystemResources.Update(r => r with { OnAcPower = true });
        f.Time.Advance(JobScheduler.DeferralCooldown + TimeSpan.FromSeconds(1));
        await RunUntilSettledAsync(f);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Heavy_work_never_runs_while_recording_but_light_work_does()
    {
        var heavy = new ScriptedHandler("t.heavy", JobResourceClass.Heavy, (_, _) => Task.CompletedTask);
        var light = new ScriptedHandler("t.light", JobResourceClass.Light, (_, _) => Task.CompletedTask);
        await using var f = await CreateAsync(heavy, light);
        var session = await f.Recording.StartAsync(new Kakitome.Application.Recording.RecordingOptions());

        var h = await f.Scheduler.EnqueueAsync(new JobRequest("t.heavy"));
        var l = await f.Scheduler.EnqueueAsync(new JobRequest("t.light"));
        await RunUntilSettledAsync(f);

        Assert.Equal(JobWaitReason.RecordingInProgress, (await f.Jobs.GetAsync(h.Id))!.WaitReason);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(l.Id))!.State);

        await session.StopAsync();
        await RunUntilSettledAsync(f);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(h.Id))!.State);
    }

    [Fact]
    public async Task Only_one_heavy_job_runs_at_a_time()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var heavy = new ScriptedHandler("t.heavy", JobResourceClass.Heavy, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrent);
        });
        await using var f = await CreateAsync(heavy);
        for (var i = 0; i < 3; i++)
        {
            await f.Scheduler.EnqueueAsync(new JobRequest("t.heavy"));
        }

        await RunUntilSettledAsync(f, passes: 12);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(3, heavy.Runs);
    }

    [Fact]
    public async Task Jobs_interrupted_by_a_crash_resume_from_their_checkpoint_after_restart()
    {
        var checkpoints = new List<string?>();
        var handler = new ScriptedHandler("t.resumable", JobResourceClass.Light, (ctx, _) =>
        {
            lock (checkpoints)
            {
                checkpoints.Add(ctx.Job.Checkpoint);
            }

            return Task.CompletedTask;
        });
        await using var f = await CreateAsync(handler);
        var job = await f.Scheduler.EnqueueAsync(new JobRequest("t.resumable"));

        // Simulate a process that died mid-job: the row says Running with a checkpoint.
        await f.Jobs.UpdateAsync(job with { State = JobState.Running, Checkpoint = "chunk-7" });
        await f.RestartAsync();

        await f.Scheduler.RecoverInterruptedAsync();
        Assert.Equal(JobState.Pending, (await f.Jobs.GetAsync(job.Id))!.State);
        await RunUntilSettledAsync(f);

        Assert.Equal(["chunk-7"], checkpoints);
        Assert.Equal(JobState.Succeeded, (await f.Jobs.GetAsync(job.Id))!.State);
    }

    [Fact]
    public async Task Jobs_of_an_unknown_kind_fail_instead_of_blocking_the_queue()
    {
        await using var f = await CreateAsync();
        var now = f.Time.GetUtcNow();
        var orphan = new JobRecord { Id = Guid.CreateVersion7(), Kind = "from.a.newer.version", CreatedAt = now, UpdatedAt = now };
        await f.Jobs.AddAsync(orphan);

        await RunUntilSettledAsync(f);

        var failed = (await f.Jobs.GetAsync(orphan.Id))!;
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("from.a.newer.version", failed.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enqueueing_an_unknown_kind_is_rejected()
    {
        await using var f = await CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Scheduler.EnqueueAsync(new JobRequest("nope")));
    }

    internal static Task<LibraryFixture> CreateAsync(params ScriptedHandler[] handlers) =>
        LibraryFixture.CreateAsync(configure: services =>
        {
            foreach (var handler in handlers)
            {
                services.AddSingleton<IJobHandler>(handler);
            }
        });

    /// <summary>Runs scheduling passes until nothing is running and the last pass started nothing new.</summary>
    internal static async Task RunUntilSettledAsync(LibraryFixture f, int passes = 6)
    {
        for (var i = 0; i < passes; i++)
        {
            await f.Scheduler.TickAsync();
            await f.Scheduler.WhenIdleAsync();
        }
    }

    private static async Task WaitForStateAsync(LibraryFixture f, Guid id, JobState state) =>
        await WaitUntilAsync(() => f.Jobs.GetAsync(id).Result?.State == state);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not reached");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>Job handler whose behavior is supplied by the test.</summary>
public sealed class ScriptedHandler(string kind, JobResourceClass resourceClass, Func<JobContext, CancellationToken, Task> body) : IJobHandler
{
    private int _runs;

    public string Kind { get; } = kind;

    public JobResourceClass ResourceClass { get; } = resourceClass;

    public int Runs => Volatile.Read(ref _runs);

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        return body(context, cancellationToken);
    }
}
