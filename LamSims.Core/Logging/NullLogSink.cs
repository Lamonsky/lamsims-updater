namespace LamSims.Core.Logging;

/// <summary>
/// The default every constructor falls back to, so no call site needs a null check and no test
/// that does not care about logging needs a stub.
/// </summary>
public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();

    private NullLogSink() { }

    public void Write(LogLine line) { }
}
