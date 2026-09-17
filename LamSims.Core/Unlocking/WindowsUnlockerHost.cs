using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace LamSims.Core.Unlocking;

/// <summary>
/// The only Windows-specific class here. CA1416 enforces the platform annotation even on Linux
/// builds, but does NOT cover <see cref="DeleteScheduledTask"/>, which shells out to schtasks.exe
/// — that member is guarded by <see cref="IsAvailable"/> instead.
/// </summary>
public sealed class WindowsUnlockerHost : IUnlockerHost
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    private static readonly Dictionary<ClientRegistryKey, string> Keys = new()
    {
        [ClientRegistryKey.EaDesktop] = @"SOFTWARE\Electronic Arts\EA Desktop",
        [ClientRegistryKey.EaDesktopWow6432] = @"SOFTWARE\WOW6432Node\Electronic Arts\EA Desktop",
        [ClientRegistryKey.OriginWow6432] = @"SOFTWARE\WOW6432Node\Origin",
        [ClientRegistryKey.Origin] = @"SOFTWARE\Origin",
    };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadValue(RegistryKey root, string subKey, string name)
    {
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    public string? ReadClientPath(ClientRegistryKey key)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ReadValue(Registry.LocalMachine, Keys[key], "ClientPath");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                      or IOException)
        {
            // A missing or unreadable key must not throw: detection walks every candidate in order.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static AutostartValue? ReadAutostart(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);

        // DoNotExpandEnvironmentNames: read expanded, a REG_EXPAND_SZ entry would be written back as
        // one machine's answer to "%ProgramFiles%" instead of the variable the user had.
        var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null || key is null) return null;

        var kind = key.GetValueKind(name) switch
        {
            RegistryValueKind.String => AutostartValueKind.String,
            RegistryValueKind.ExpandString => AutostartValueKind.ExpandString,
            _ => AutostartValueKind.Unsupported,
        };

        return new AutostartValue(
            Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            kind);
    }

    public AutostartValue? ReadAutostartValue(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return ReadAutostart(name);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                      or IOException)
        {
            return null;
        }
    }

    public void WriteAutostartValue(string name, AutostartValue value)
    {
        if (!OperatingSystem.IsWindows()) return;

        // CreateSubKey, not OpenSubKey: the Run key is absent on an account that never had an
        // autostart entry, and the null the latter returns becomes a silent no-op below. Read and
        // remove keep OpenSubKey, where a missing key really does mean nothing to do.
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.SetValue(name, value.Value,
            value.Kind == AutostartValueKind.ExpandString
                ? RegistryValueKind.ExpandString
                : RegistryValueKind.String);
    }

    public void RemoveAutostartValue(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(name) is not null) key.DeleteValue(name);
    }

    public IReadOnlyList<string> RunningClientProcesses(IReadOnlyList<string> processNames)
    {
        var matches = Matching(processNames);
        try
        {
            return matches.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
        }
        finally
        {
            foreach (var (_, process) in matches) process.Dispose();
        }
    }

    public IReadOnlyList<string> KillClientProcesses(IReadOnlyList<string> processNames,
                                                    TimeSpan perProcessTimeout)
    {
        var survivors = new List<string>();

        foreach (var (name, process) in Matching(processNames))
        {
            using (process)
            {
                try
                {
                    // Not the process tree. The tree is whatever the client launched, and for the
                    // EA app that includes the running game, so only the exactly named processes
                    // are killed.
                    process.Kill();

                    // The wait result is kept: a client that outlives its timeout still holds the
                    // client directory open, and the caller has to know that now.
                    if (!process.WaitForExit((int)perProcessTimeout.TotalMilliseconds))
                        survivors.Add(name);
                }
                catch (Exception e) when (e is InvalidOperationException or SystemException
                                              or AggregateException)
                {
                    // Gone between enumeration and kill, or not ours. The probe needs its own guard:
                    // it reads the handle and can throw, and a throw inside a catch body is not
                    // filtered by that catch.
                    if (!HasExited(process)) survivors.Add(name);
                }
            }
        }

        return survivors.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Fails closed: a probe that cannot answer is treated as "still running", so the
    /// install refuses rather than writing into a directory the client may still hold open.</summary>
    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception e) when (e is InvalidOperationException or SystemException) { return false; }
    }

    // Exact-name lookup only; the name set belongs to the backend, so this file holds no policy.
    // The name travels beside the handle because Process.ProcessName throws once the process has
    // exited, which is exactly the moment the kill path needs to name it.
    private static List<(string Name, Process Process)> Matching(IReadOnlyList<string> processNames) =>
        processNames
            .SelectMany(name =>
            {
                try
                {
                    return Process.GetProcessesByName(name).Select(p => (Name: name, Process: p));
                }
                catch (InvalidOperationException)
                {
                    return Enumerable.Empty<(string Name, Process Process)>();
                }
            })
            .ToList();

    public void DeleteScheduledTask(string name)
    {
        if (!OperatingSystem.IsWindows()) return;

        // schtasks rather than a task-scheduler package: a single delete is all that is needed, and
        // it adds no dependency.
        //
        // Queried before deleted, because /Delete's exit code cannot tell "there was no task", the
        // normal case here, from "you may not touch it", and its messages are localised. /Query
        // settles the first question on its exit code alone, so a /Delete that fails afterwards is a
        // real refusal, and the caller turns it into a warning rather than reporting a removal that
        // did not happen.
        if (Run("/Query", "/TN", name) != 0) return;
        if (Run("/Delete", "/TN", name, "/F") != 0)
            throw new IOException($"The '{name}' scheduled task exists but could not be deleted.");
    }

    /// <summary>schtasks' exit code, or a non-zero stand-in when it could not be started or outlived
    /// its timeout. Every caller reads non-zero as "this did not happen".</summary>
    private static int Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return -1;

            // Redirected streams have to be drained or the child blocks once a pipe buffer fills.
            // One task's worth of output cannot fill either buffer, so reading them in turn is safe
            // here in a way it would not be for an unbounded command.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            return process.WaitForExit(10_000) ? process.ExitCode : -1;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }

    public bool TryRelaunchElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        try
        {
            // "runas" raises the UAC prompt. A declined prompt throws Win32Exception with
            // ERROR_CANCELLED (1223), which is a false return and not a failure: the caller must
            // stay running, because it has not shut anything down yet.
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",

                // Elevated ShellExecute does not reliably inherit the current directory, and the
                // arguments below can name paths relative to it.
                WorkingDirectory = Environment.CurrentDirectory,
            };

            // Forwarded, or the elevated instance comes up without the command-line catalog that
            // outranks every other source, landing the user in a differently configured app.
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
                info.ArgumentList.Add(argument);

            using var started = Process.Start(info);
            return started is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
