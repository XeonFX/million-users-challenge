using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

[assembly: AssemblyFixture(typeof(CodeFusion.Tests.RunGuardCheck))]

namespace CodeFusion.Tests;

/// <summary>
/// Fails the run on an exception nothing caught or observed, which would otherwise pass unnoticed: one that ended a
/// thread (the runtime takes the process down; this says which exception did it), or a faulted Task nobody awaited
/// (the runtime only reports it when the GC finalizes the task, and then ignores it). Linked into every test project.
/// </summary>
internal static class RunGuard
{
    static readonly List<Exception> Unobserved = [];

    [ModuleInitializer]
    internal static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"unhandled exception, the test run fails: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            lock (Unobserved) Unobserved.Add(e.Exception);
        };
    }

    /// <summary>Finalizes the tasks the GC can reach, so their unobserved exceptions are reported now.</summary>
    internal static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Takes back the recorded exceptions that mention <paramref name="marker"/>: a test of the guard
    /// itself, so its own exception doesn't fail the run.</summary>
    internal static List<Exception> Take(string marker)
    {
        lock (Unobserved)
        {
            var taken = Unobserved.FindAll(e => e.ToString().Contains(marker, StringComparison.Ordinal));
            Unobserved.RemoveAll(taken.Contains);
            return taken;
        }
    }

    /// <summary>What was recorded and not taken back, one exception per line.</summary>
    internal static string? Report()
    {
        lock (Unobserved)
            return Unobserved.Count == 0 ? null
                : $"{Unobserved.Count} task exception(s) were never observed:\n{string.Join("\n", Unobserved)}";
    }
}

/// <summary>Runs after the assembly's last test: an unobserved exception fails the run.</summary>
public sealed class RunGuardCheck : IDisposable
{
    public void Dispose()
    {
        RunGuard.Collect();
        if (RunGuard.Report() is { } report) throw new InvalidOperationException(report);
    }
}

public sealed class RunGuardTests
{
    [Fact]
    public void AFaultedTaskNobodyAwaitedIsRecorded()
    {
        string marker = $"dropped on purpose {Guid.NewGuid():N}";
        DropFaultedTask(marker);
        Assert.Single(Wait(marker));
    }

    /// <summary>Waits until the guard recorded the dropped task, and takes it back so the run passes.</summary>
    static List<Exception> Wait(string marker)
    {
        for (int i = 0; i < 50; i++)
        {
            RunGuard.Collect();
            List<Exception> taken = RunGuard.Take(marker);
            if (taken.Count > 0) return taken;
            Thread.Sleep(10);
        }
        return [];
    }

    /// <summary>Not inlined, so no local of the caller keeps the task alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void DropFaultedTask(string marker)
    {
        Task t = Task.Run(() => throw new InvalidOperationException(marker));
        // IsCompleted doesn't observe the exception; Wait or Exception would
        SpinWait.SpinUntil(() => t.IsCompleted);
    }
}
