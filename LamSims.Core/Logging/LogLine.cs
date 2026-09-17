namespace LamSims.Core.Logging;

public enum LogSeverity { Info, Warning, Error }

/// <summary>
/// One line for the log view. Deliberately carries NO timestamp: IClock lives in
/// LamSims.App/Services and Core has no clock of its own, so Core says what happened and the App
/// stamps when. That is also what makes the stamps assertable, which an inline DateTime.Now
/// would not be.
/// </summary>
/// <param name="Code">The pack or client this is about, or null for application-wide lines.</param>
public sealed record LogLine(string? Code, string Text, LogSeverity Severity)
{
    public static LogLine Info(string text, string? code = null) => new(code, text, LogSeverity.Info);

    public static LogLine Warning(string text, string? code = null) =>
        new(code, text, LogSeverity.Warning);

    public static LogLine Error(string text, string? code = null) => new(code, text, LogSeverity.Error);
}
