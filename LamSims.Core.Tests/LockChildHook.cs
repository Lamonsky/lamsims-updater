using System;
using System.IO;
using System.Threading;
using System.Runtime.CompilerServices;
using LamSims.Core.Downloading;
using LamSims.Core.Queueing;

namespace LamSims.Core.Tests;

/// <summary>
/// Lets the cross-process lock test re-enter this assembly as a child process: it acquires the
/// lock, signals readiness with a file, and holds until a release file appears. A module
/// initializer runs before any test host code, so the child never starts a test run.
///
/// The token carries a value the parent generated for that one run. A plain variable name would
/// let one exported in a developer shell turn an ordinary test run into a child, which takes the
/// lock and exits green without running a test. Past the gate every unfinished path exits non-zero.
/// </summary>
internal static class LockChildHook
{
    [ModuleInitializer]
    internal static void Run()
    {
        // No token means an ordinary test run, which must proceed; every other exit below is loud.
        var token = Environment.GetEnvironmentVariable("LAMSIMS_LOCK_CHILD_TOKEN");
        if (token is null) return;

        var root = Environment.GetEnvironmentVariable("LAMSIMS_LOCK_CHILD_ROOT");
        var ready = Environment.GetEnvironmentVariable("LAMSIMS_LOCK_CHILD_READY");
        var release = Environment.GetEnvironmentVariable("LAMSIMS_LOCK_CHILD_RELEASE");

        // Committed to being a child from here on, so a half-configured environment is a failure
        // rather than a reason to fall through into a test run. The return after each exit is
        // for the compiler's flow analysis: Environment.Exit is not annotated as unreachable.
        if (root is null || ready is null || release is null)
        {
            Environment.Exit(2);
            return;
        }

        using var held = PackLock.TryAcquire(new DownloadPaths(root), "EP01");
        if (held is null)
        {
            Environment.Exit(2);
            return;
        }

        // Written under a scratch name and renamed, so the parent cannot observe the marker
        // existing but still empty and read a torn token back.
        var scratch = ready + ".tmp";
        File.WriteAllText(scratch, token);
        File.Move(scratch, ready, overwrite: true);

        // Bounded so a killed parent cannot leave this process alive forever.
        for (var i = 0; i < 600 && !File.Exists(release); i++) Thread.Sleep(50);

        // A release that never arrived means the parent died or hung: say so rather than
        // reporting success for a job that was cut short.
        Environment.Exit(File.Exists(release) ? 0 : 2);
    }
}
