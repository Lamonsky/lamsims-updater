namespace LamSims.Core.Logging;

/// <summary>
/// Where Core reports what it is doing, for a human to read. Not diagnostics and not telemetry:
/// a line here is written to be shown.
///
/// Implementations are called from any thread and from several at once — a segmented download
/// runs sixteen connections and each of them may report. An implementation that is not
/// thread-safe is a defect in the implementation, never in the callers, because no caller is in
/// a position to know which other thread is also writing.
/// </summary>
public interface ILogSink
{
    void Write(LogLine line);
}
