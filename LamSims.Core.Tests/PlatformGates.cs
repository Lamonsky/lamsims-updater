using System;
using System.IO;
using Xunit;

namespace LamSims.Core.Tests;

/// <summary>
/// A fact that runs on Linux only, reported as skipped elsewhere.
///
/// The Wine backend answers IsSupported on Linux and nowhere else, and the tests that carry this
/// attribute drive it through a prefix built on the real filesystem: a drive_c tree, dosdevices
/// entries that map a drive letter to a directory, Windows-to-Unix path translation, POSIX modes,
/// and a fake /proc. On Windows and macOS those constructs do not mean what the prefix format says
/// they mean — a backslash path is not a foreign path, dosdevices is not consulted, and there is no
/// /proc to attribute a process through — so the assertions there are neither passing nor failing
/// for a reason connected to the code.
///
/// This is not a substitute for the in-body <c>OperatingSystem.IsWindows()</c> guards. Those exist
/// to satisfy the platform analyser, which cannot see this attribute, and are still required
/// wherever a test calls a POSIX-only API.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    /// <param name="because">
    /// Overrides the default reason, for a test that is Linux-only for its own reason rather than
    /// because it drives the Wine backend.
    /// </param>
    public LinuxFactAttribute(string? because = null)
    {
        if (!OperatingSystem.IsLinux()) Skip = because ?? Reason;
    }

    internal const string Reason =
        "Linux only: the Wine unlocker backend is supported on Linux, and this test drives it "
        + "through a prefix on the real filesystem.";
}

/// <summary>The <see cref="LinuxFactAttribute"/> counterpart for a data-driven test.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = LinuxFactAttribute.Reason;
    }
}

/// <summary>
/// Whether a Unix file mode this suite sets is actually enforced against it.
///
/// Several tests arrange a failure by making a directory unwritable or unreadable and then assert
/// on the diagnostic the refusal produces. Running as uid 0 bypasses the mode, so the arranged
/// failure never happens, the code under test succeeds, and the assertion fails for a reason that
/// has nothing to do with the code. The <c>root-check</c> workflow is what found them, and now
/// stays green as long as every such test carries this gate.
///
/// This asks for the refusal rather than comparing a uid, because uid 0 is only the common way to
/// arrive here: a filesystem that does not carry a mode at all produces the same problem, and a
/// probe answers for whatever the suite is actually running on.
/// </summary>
internal static class PosixDenial
{
    private static readonly Lazy<bool> Probed = new(Probe);

    internal static bool Enforced => Probed.Value;

    internal const string Reason =
        "The filesystem does not enforce a Unix file mode against this process, so the refusal "
        + "this test arranges never happens. Its subject is the diagnostic that refusal produces, "
        + "and without a refusal there is nothing to observe. Running as uid 0 is the usual cause.";

    private static bool Probe()
    {
        if (OperatingSystem.IsWindows()) return false;

        var root = Path.Combine(
            Path.GetTempPath(), "lamsims-denial-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                File.WriteAllText(Path.Combine(root, "probe"), "");
                return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(
                    root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A probe directory left behind under the temp root is not worth failing over.
            }
        }
    }
}

/// <summary>
/// A fact that runs only where <see cref="PosixDenial.Enforced"/> holds, reported as skipped
/// elsewhere, including on Windows.
///
/// The in-body <c>OperatingSystem.IsWindows()</c> guards these tests carry are still required:
/// the platform analyser cannot see this attribute, and the POSIX calls they make are not
/// callable on Windows whatever the runtime does.
/// </summary>
public sealed class PosixDenialFactAttribute : FactAttribute
{
    public PosixDenialFactAttribute()
    {
        if (!PosixDenial.Enforced) Skip = PosixDenial.Reason;
    }
}
