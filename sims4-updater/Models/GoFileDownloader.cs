using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace sims4_updater.Models;
public sealed class GoFileDownloader : IDisposable
{
    private const string ApiBaseUrl = "https://api.gofile.io";
    private const string DefaultUserAgent = "Mozilla/5.0";
    private const int WebsiteTokenSlotSeconds = 14400;
    private const int RetryDelaySeconds = 5;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly int _maxRetries;
    private readonly int _maxConcurrency;
    private readonly int _bufferSize;
    private readonly TimeSpan _timeout;

    public event EventHandler<GoFileDownloadProgress>? ProgressChanged;

    public GoFileDownloader(
        HttpClient? httpClient = null,
        int maxRetries = 5,
        int maxConcurrency = 5,
        int bufferSize = 1024 * 1024,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        _disposeHttpClient = httpClient is null;
        _maxRetries = Math.Max(1, maxRetries);
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _bufferSize = Math.Max(4096, bufferSize);
        _timeout = timeout ?? TimeSpan.FromSeconds(30);

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }

        _httpClient.DefaultRequestHeaders.AcceptEncoding.Clear();
        _httpClient.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://gofile.io/");
    }

    public async Task<bool> DownloadAsync(
        string url,
        string destinationDirectory,
        string? password = null,
        string? accountToken = null,
        CancellationToken cancellationToken = default,
        bool preserveDirectoryStructure = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        if (TryGetContentId(url, out string contentId))
        {
            await SetAccountTokenAsync(accountToken, cancellationToken);
            List<RemoteFile> files = await GetContentFilesAsync(contentId, password, cancellationToken);
            if (files.Count == 0)
            {
                return false;
            }

            string contentDirectory = preserveDirectoryStructure
                ? Path.Combine(destinationDirectory, contentId)
                : destinationDirectory;
            Directory.CreateDirectory(contentDirectory);
            return await DownloadFilesAsync(files, contentDirectory, cancellationToken, preserveDirectoryStructure);
        }

        string fileName = GetFileNameFromUrl(url);
        return await DownloadFileWithRetryAsync(new RemoteFile(fileName, url, string.Empty), destinationDirectory, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<List<RemoteFile>> GetContentFilesAsync(string contentId, string? password, CancellationToken cancellationToken)
    {
        string url = $"{ApiBaseUrl}/contents/{Uri.EscapeDataString(contentId)}?cache=true&sortField=createTime&sortDirection=1";
        if (!string.IsNullOrWhiteSpace(password))
        {
            string passwordHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
            url += $"&password={Uri.EscapeDataString(passwordHash)}";
        }

        using JsonDocument document = await GetJsonWithRetryAsync(url, cancellationToken);
        JsonElement root = document.RootElement;
        EnsureApiSuccess(root);
        List<RemoteFile> files = new();
        await ReadContentNodeAsync(root.GetProperty("data"), string.Empty, files, cancellationToken);
        return files;
    }

    private async Task ReadContentNodeAsync(JsonElement node, string relativeDirectory, List<RemoteFile> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.GetProperty("type").GetString() != "folder")
        {
            files.Add(new RemoteFile(
                SanitizeFileName(node.GetProperty("name").GetString() ?? "download"),
                node.GetProperty("link").GetString() ?? throw new InvalidOperationException("GoFile returned a file without a link."),
                relativeDirectory));
            return;
        }

        string folderName = SanitizeFileName(node.GetProperty("name").GetString() ?? "root");
        string nextDirectory = string.IsNullOrEmpty(relativeDirectory)
            ? folderName == "root" ? string.Empty : folderName
            : Path.Combine(relativeDirectory, folderName);

        if (!node.TryGetProperty("children", out JsonElement children))
        {
            return;
        }

        foreach (JsonProperty child in children.EnumerateObject())
        {
            await ReadContentNodeAsync(child.Value, nextDirectory, files, cancellationToken);
        }
    }

    private async Task<bool> DownloadFilesAsync(
        IReadOnlyCollection<RemoteFile> files,
        string rootDirectory,
        CancellationToken cancellationToken,
        bool preserveDirectoryStructure)
    {
        int completed = 0;
        using SemaphoreSlim semaphore = new(_maxConcurrency);
        List<Task<bool>> tasks = files.Select(async file =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                bool result = await DownloadFileWithRetryAsync(file, rootDirectory, cancellationToken, preserveDirectoryStructure);
                int completedCount = Interlocked.Increment(ref completed);
                ProgressChanged?.Invoke(this, new GoFileDownloadProgress(file.FileName, completedCount, files.Count, result ? 100 : 0));
                return result;
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        bool[] results = await Task.WhenAll(tasks);
        return results.All(result => result);
    }

    private async Task<bool> DownloadFileWithRetryAsync(
        RemoteFile file,
        string rootDirectory,
        CancellationToken cancellationToken,
        bool preserveDirectoryStructure = true)
    {
        string directory = preserveDirectoryStructure
            ? Path.Combine(rootDirectory, file.RelativeDirectory)
            : rootDirectory;
        Directory.CreateDirectory(directory);
        string destinationPath = Path.Combine(directory, file.FileName);
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return true;
        }

        string temporaryPath = destinationPath + ".part";
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await DownloadFileAsync(file, destinationPath, temporaryPath, cancellationToken, true);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
            }
            catch (IOException) when (attempt < _maxRetries)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _maxRetries)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds * attempt), cancellationToken);
        }

        return false;
    }

    private async Task<bool> DownloadFileAsync(
        RemoteFile file,
        string destinationPath,
        string temporaryPath,
        CancellationToken cancellationToken,
        bool resumePartialFile)
    {
        long existingLength = resumePartialFile && File.Exists(temporaryPath)
            ? new FileInfo(temporaryPath).Length
            : 0;
        using HttpRequestMessage request = new(HttpMethod.Get, file.Url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
        {
            File.Delete(temporaryPath);
            return await DownloadFileAsync(file, destinationPath, temporaryPath, cancellationToken, false);
        }

        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            existingLength = 0;
            File.Delete(temporaryPath);
        }

        response.EnsureSuccessStatusCode();
        long totalLength = response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength.HasValue ? existingLength + response.Content.Headers.ContentLength.Value : 0);
        long downloaded = existingLength;
        {
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(temporaryPath, FileMode.Append, FileAccess.Write, FileShare.None, _bufferSize, true);
            byte[] buffer = new byte[_bufferSize];
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloaded += bytesRead;
                ProgressChanged?.Invoke(this, new GoFileDownloadProgress(file.FileName, downloaded, totalLength, totalLength > 0 ? downloaded * 100d / totalLength : 0));
            }

            await output.FlushAsync(cancellationToken);
        }

        if (totalLength > 0 && downloaded != totalLength)
        {
            throw new IOException($"Incomplete download of {file.FileName}.");
        }

        File.Move(temporaryPath, destinationPath, true);
        return true;
    }

    private async Task SetAccountTokenAsync(string? accountToken, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accountToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
            return;
        }

        // Reuse the guest account from the previous download instead of creating a new one
        if (_httpClient.DefaultRequestHeaders.Authorization is not null)
        {
            return;
        }

        string websiteToken = CreateWebsiteToken(_httpClient.DefaultRequestHeaders.UserAgent.ToString(), string.Empty);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{ApiBaseUrl}/accounts");
        request.Headers.Add("X-Website-Token", websiteToken);
        request.Headers.Add("X-BL", "en-US");
        using JsonDocument document = await SendJsonAsync(request, cancellationToken);
        EnsureApiSuccess(document.RootElement);
        string token = document.RootElement.GetProperty("data").GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GoFile did not return an account token.");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<JsonDocument> GetJsonWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Add("X-Website-Token", CreateWebsiteToken(_httpClient.DefaultRequestHeaders.UserAgent.ToString(), GetAccountToken()));
            request.Headers.Add("X-BL", "en-US");
            try
            {
                return await SendJsonAsync(request, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _maxRetries)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds * attempt), cancellationToken);
        }
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    private string GetAccountToken() => _httpClient.DefaultRequestHeaders.Authorization?.Parameter ?? string.Empty;

    private static string CreateWebsiteToken(string userAgent, string accountToken)
    {
        long timeSlot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / WebsiteTokenSlotSeconds;
        string raw = $"{userAgent}::en-US::{accountToken}::{timeSlot}::12af056dacea0b";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static void EnsureApiSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("status", out JsonElement status) || status.GetString() != "ok")
        {
            throw new InvalidOperationException("GoFile API returned an unsuccessful response.");
        }
    }

    private static bool TryGetContentId(string url, out string contentId)
    {
        contentId = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.Host.EndsWith("gofile.io", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int downloadIndex = Array.FindIndex(segments, segment => segment.Equals("d", StringComparison.OrdinalIgnoreCase));
        int contentsIndex = Array.FindIndex(segments, segment => segment.Equals("contents", StringComparison.OrdinalIgnoreCase));
        int contentIndex = downloadIndex >= 0 ? downloadIndex + 1 : contentsIndex >= 0 ? contentsIndex + 1 : -1;
        if (contentIndex < 0 || contentIndex >= segments.Length)
        {
            return false;
        }

        contentId = segments[contentIndex];
        return !string.IsNullOrWhiteSpace(contentId);
    }

    private static string GetFileNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return SanitizeFileName(name);
            }
        }

        return "download";
    }

    private static string SanitizeFileName(string name)
    {
        string sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".." ? "download" : sanitized;
    }

    private sealed record RemoteFile(string FileName, string Url, string RelativeDirectory);
}

public sealed record GoFileDownloadProgress(string FileName, long BytesDownloaded, long TotalBytes, double Percentage);