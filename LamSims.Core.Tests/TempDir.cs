using System;
using System.IO;
using System.Runtime.Versioning;

namespace LamSims.Core.Tests;

/// <summary>A directory under the system temp root, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lamsims-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Write(string name, string contents) => System.IO.File.WriteAllText(File(name), contents);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

/// <summary>
/// A directory nothing can be created inside, for exercising permission failures. Write
/// access is restored on dispose so the enclosing <see cref="TempDir"/> can still be deleted.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ReadOnlyDir : IDisposable
{
    private const UnixFileMode Locked = UnixFileMode.UserRead | UnixFileMode.UserExecute;
    private const UnixFileMode Unlocked = Locked | UnixFileMode.UserWrite;

    private readonly string _root;

    /// <summary>A path inside the locked directory. It does not exist and cannot be created.</summary>
    public string Child { get; }

    public ReadOnlyDir(string parent)
    {
        _root = System.IO.Path.Combine(parent, "read-only");
        Directory.CreateDirectory(_root);
        Child = System.IO.Path.Combine(_root, "downloads");
        System.IO.File.SetUnixFileMode(_root, Locked);
    }

    public void Dispose() => System.IO.File.SetUnixFileMode(_root, Unlocked);
}
