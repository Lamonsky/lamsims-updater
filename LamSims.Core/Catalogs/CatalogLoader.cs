using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using LamSims.Core.Logging;
using LamSims.Core.Settings;

namespace LamSims.Core.Catalogs;

/// <summary>The four sources, in the order they are tried.</summary>
public enum CatalogSourceKind { CommandLine, Settings, BesideExecutable, Cache }

public sealed record CatalogSource(CatalogSourceKind Kind, string Location)
{
    public bool IsRemote =>
        Uri.TryCreate(Location, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public enum CatalogStatus { Loaded, Empty, Failed }

/// <summary>The catalog is too large to be a genuine catalog and was refused unparsed.</summary>
public sealed class CatalogTooLargeException : IOException
{
    public CatalogTooLargeException(string message) : base(message) { }
}

/// <summary>The outcome of resolution. <see cref="CatalogStatus.Empty"/> is not an error — the
/// user has not chosen a catalog yet. <see cref="CachedCopy"/> is offered as an explicit choice,
/// never substituted automatically, and is the source rather than a flag so the caller can load
/// it.</summary>
public sealed record CatalogResolution(
    CatalogStatus Status,
    CatalogLoadResult? Load,
    CatalogSource? Source,
    string? Error,
    CatalogSource? CachedCopy);

public sealed class CatalogLoader
{
    /// <summary>The cap, wherever the catalog comes from: a local path is a value the user typed,
    /// no more trustworthy than a URL.</summary>
    private const long MaxCatalogBytes = 1024 * 1024;

    private static readonly TimeSpan DefaultRemoteTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly AppPaths _paths;
    private readonly string _executableDirectory;
    private readonly TimeSpan _remoteTimeout;
    private readonly ILogSink _log;

    /// <param name="remoteTimeout">
    /// Bounds a remote fetch end to end. <see cref="HttpFactory"/> hands out a client with an
    /// infinite timeout by design, since the download engine carries its own per-read deadlines —
    /// reused here that would hang forever against a mirror that says nothing.
    /// </param>
    public CatalogLoader(
        HttpClient client, AppPaths paths, string? executableDirectory = null,
        TimeSpan? remoteTimeout = null, ILogSink? log = null)
    {
        _client = client;
        _paths = paths;
        _executableDirectory = executableDirectory ?? AppContext.BaseDirectory;
        _remoteTimeout = remoteTimeout ?? DefaultRemoteTimeout;
        _log = log ?? NullLogSink.Instance;
    }

    /// <summary>The sources to try, in order. A command-line or settings value counts as present
    /// whenever it is non-blank, even if nothing is there: a silent fall-through hides a typo.</summary>
    public IReadOnlyList<CatalogSource> Candidates(string? commandLine, string? fromSettings)
    {
        var candidates = new List<CatalogSource>();

        if (!string.IsNullOrWhiteSpace(commandLine))
            candidates.Add(new CatalogSource(CatalogSourceKind.CommandLine, commandLine));

        if (!string.IsNullOrWhiteSpace(fromSettings))
            candidates.Add(new CatalogSource(CatalogSourceKind.Settings, fromSettings));

        var beside = Path.Combine(_executableDirectory, "catalog.json");
        if (File.Exists(beside))
            candidates.Add(new CatalogSource(CatalogSourceKind.BesideExecutable, beside));

        if (File.Exists(_paths.CatalogCacheFile))
            candidates.Add(new CatalogSource(CatalogSourceKind.Cache, _paths.CatalogCacheFile));

        return candidates;
    }

    /// <summary>
    /// Propagates the caller's own cancellation as <see cref="OperationCanceledException"/>
    /// rather than folding it into a <see cref="CatalogStatus.Failed"/> result.
    /// </summary>
    public async Task<CatalogResolution> ResolveAsync(
        string? commandLine, string? fromSettings, CancellationToken ct)
    {
        var cachedCopy = File.Exists(_paths.CatalogCacheFile)
            ? new CatalogSource(CatalogSourceKind.Cache, _paths.CatalogCacheFile)
            : null;

        foreach (var candidate in Candidates(commandLine, fromSettings))
        {
            try
            {
                return new CatalogResolution(
                    CatalogStatus.Loaded, await LoadCoreAsync(candidate, ct), candidate, null, cachedCopy);
            }
            catch (Exception e) when (IsSourceFailure(e, ct))
            {
                // Once per failing candidate, reported or not: a source stepped over silently in
                // the resolution must still say so here, or a corrupt cache fails invisibly.
                _log.Write(LogLine.Error($"Catalog load failed: {FailureMessage(candidate, e)}"));

                // A cache that just failed is known broken and must not be offered as the fallback.
                // Nothing else failing says anything about it.
                if (candidate.Kind == CatalogSourceKind.Cache)
                    cachedCopy = null;

                // Only the first two sources are the user's explicit choice. A failure there is
                // reported, and the cached copy is offered as a choice rather than substituted.
                // The last two are fallbacks nobody asked for, so a broken one is passed over.
                if (candidate.Kind is CatalogSourceKind.CommandLine or CatalogSourceKind.Settings)
                {
                    return new CatalogResolution(
                        CatalogStatus.Failed, null, candidate, FailureMessage(candidate, e), cachedCopy);
                }
            }
        }

        return new CatalogResolution(CatalogStatus.Empty, null, null, null, cachedCopy);
    }

    /// <summary>Loads exactly <paramref name="source"/> — the cached copy the user chose, or a
    /// retry. A load failure comes back as <see cref="CatalogStatus.Failed"/> rather than throwing;
    /// the caller's own cancellation still propagates.</summary>
    public async Task<CatalogResolution> LoadAsync(CatalogSource source, CancellationToken ct)
    {
        try
        {
            return new CatalogResolution(
                CatalogStatus.Loaded, await LoadCoreAsync(source, ct), source, null, null);
        }
        catch (Exception e) when (IsSourceFailure(e, ct))
        {
            _log.Write(LogLine.Error($"Catalog load failed: {FailureMessage(source, e)}"));
            return new CatalogResolution(CatalogStatus.Failed, null, source, FailureMessage(source, e), null);
        }
    }

    /// <summary>True for a source failure: format, I/O, network, path, or this loader's deadline —
    /// which surfaces as <see cref="OperationCanceledException"/> and does not wrap into
    /// <see cref="HttpRequestException"/>. False when the CALLER's token fired, so that still
    /// propagates.</summary>
    private static bool IsSourceFailure(Exception e, CancellationToken ct) =>
        e is CatalogFormatException or IOException or HttpRequestException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException
        || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private static string FailureMessage(CatalogSource source, Exception e) =>
        $"The catalog at '{source.Location}' could not be loaded: {e.Message}";

    /// <summary>Shared by <see cref="ResolveAsync"/> and <see cref="LoadAsync"/>, so the source
    /// and count lines fire on every path that attempts a load. Failures are logged by the caller,
    /// each of which wraps exactly one call here, so a failure fires exactly once per attempt.</summary>
    private async Task<CatalogLoadResult> LoadCoreAsync(CatalogSource source, CancellationToken ct)
    {
        _log.Write(LogLine.Info($"Loading a catalog from {source.Kind}: {source.Location}"));

        var result = source.IsRemote
            ? await LoadRemoteAsync(source, ct)
            : CatalogParser.Parse(await ReadLocalBoundedAsync(source, ct));

        _log.Write(LogLine.Info(
            $"Loaded {result.Catalog.Packs.Count} packs, {result.Rejected.Count} "
            + $"entr{(result.Rejected.Count == 1 ? "y" : "ies")} rejected"));

        foreach (var rejected in result.Rejected)
            _log.Write(LogLine.Warning($"{rejected.Description} rejected: {rejected.Reason}"));

        return result;
    }

    private async Task<CatalogLoadResult> LoadRemoteAsync(CatalogSource source, CancellationToken ct)
    {
        // Bounds the fetch end to end; see the constructor's remoteTimeout parameter. Firing
        // cancels the linked token, not the caller's ct, so IsSourceFailure's check of
        // ct.IsCancellationRequested still tells the two apart.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_remoteTimeout);

        // ResponseHeadersRead: without it, HttpClient buffers the entire body itself before
        // GetAsync even returns, and the size cap below would run only after a hostile or
        // misconfigured URL had already been read into memory in full.
        using var response = await _client.GetAsync(
            source.Location, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        var json = await ReadBoundedAsync(response, source, deadline.Token);

        // Parsed before it is cached, so a mirror serving an error page never becomes the
        // copy the application falls back to when it is offline.
        var result = CatalogParser.Parse(json);
        await CacheAsync(json, ct);
        return result;
    }

    /// <summary>The local counterpart of <see cref="ReadBoundedAsync"/>. File.ReadAllTextAsync
    /// would allocate whatever it was pointed at, and OutOfMemoryException kills the process rather
    /// than reporting. The length is known up front, so the file is refused before any is read.</summary>
    private static async Task<string> ReadLocalBoundedAsync(CatalogSource source, CancellationToken ct)
    {
        await using var stream = new FileStream(
            source.Location, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (stream.Length > MaxCatalogBytes)
        {
            throw new CatalogTooLargeException(
                $"The catalog at '{source.Location}' exceeds the {MaxCatalogBytes}-byte limit.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Streams the body up to <see cref="MaxCatalogBytes"/>, past which the URL is
    /// refused. This bounds memory only because <see cref="LoadCoreAsync"/> asks for
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>.</summary>
    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, CatalogSource source, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffered = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffered.Length + read > MaxCatalogBytes)
            {
                throw new CatalogTooLargeException(
                    $"The catalog at '{source.Location}' exceeds the {MaxCatalogBytes}-byte limit.");
            }

            await buffered.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        buffered.Position = 0;

        // Mirrors File.ReadAllTextAsync's BOM handling, so a catalog authored with one loads the
        // same over HTTP as from disk instead of reaching the parser as a U+FEFF it rejects.
        using var reader = new StreamReader(buffered, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task CacheAsync(string json, CancellationToken ct)
    {
        try
        {
            _paths.EnsureCreated();
            await AtomicFile.WriteAllTextAsync(_paths.CatalogCacheFile, json, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A catalog that loaded is usable whether or not it can be cached; the only cost
            // of failing here is that the next offline launch has nothing to fall back to.
        }
    }
}
