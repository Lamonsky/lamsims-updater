using CommunityToolkit.Mvvm.ComponentModel;
using sims4_updater.Helpers;
using System.IO;
using System.Net.Http;

namespace sims4_updater.Models
{
    public partial class Sims4DLC : ObservableObject
    {
        [ObservableProperty]
        private string _code = string.Empty;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _url = string.Empty;

        [ObservableProperty]
        private bool _installed = false;
        [ObservableProperty]
        private bool _toInstall = false;
        private string _downloadFolder = string.Empty;

        public async Task<bool> Download(Logger logger)
        {
            _downloadFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sims4DLCs");

            if (!System.IO.Directory.Exists(_downloadFolder))
            {
                System.IO.Directory.CreateDirectory(_downloadFolder);
            }

            string outputFilePath = System.IO.Path.Combine(_downloadFolder, $"{Code}.zip");

            if (System.IO.File.Exists(outputFilePath))
            {
                System.IO.File.Delete(outputFilePath);
            }

            logger.AddLog($"Starting download DLC: {Name}");
            logger.AddLog($"URL: {Url}");

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromHours(2);
            
            try
            {
                using var response = await httpClient.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                logger.AddLog($"File size: {FormatFileSize(totalBytes)}");
                logger.AddLog($"Initiating download to: {outputFilePath}");

                using var streamToReadFrom = await response.Content.ReadAsStreamAsync();
                using var streamToWriteTo = File.Open(outputFilePath, FileMode.Create);

                var buffer = new byte[81920]; // 80KB buffer
                long totalBytesRead = 0;
                int bytesRead;
                var lastProgressUpdate = DateTime.Now;

                while ((bytesRead = await streamToReadFrom.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await streamToWriteTo.WriteAsync(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if ((DateTime.Now - lastProgressUpdate).TotalSeconds >= 1)
                    {
                        if (totalBytes > 0)
                        {
                            var progressPercentage = (int)((totalBytesRead * 100) / totalBytes);
                            StaticsVariables.Instance.Progress = progressPercentage;
                        }

                        StaticsVariables.Instance.DownloadSizeInfo = 
                            $"{FormatFileSize(totalBytesRead)} / {FormatFileSize(totalBytes)}";
                        logger.AddLog($"Download progress: {FormatFileSize(totalBytesRead)} / {FormatFileSize(totalBytes)}");
                        lastProgressUpdate = DateTime.Now;
                    }
                }

                StaticsVariables.Instance.Progress = 100;
                StaticsVariables.Instance.DownloadSizeInfo = 
                    $"{FormatFileSize(totalBytesRead)} / {FormatFileSize(totalBytesRead)}";

                logger.AddLog($"Download completed: {FormatFileSize(totalBytesRead)}");
                return true;
            }
            catch (Exception ex)
            {
                logger.AddLog($"Download failed: {ex.Message}");
                return false;
            }
        }

        public void Extract(Logger logger)
        {
            string downloadFilePath = string.Empty;

            downloadFilePath = System.IO.Path.Combine(_downloadFolder, $"{Code}.zip");

            if (string.IsNullOrEmpty(downloadFilePath) || !System.IO.File.Exists(downloadFilePath))
            {
                logger.AddLog("Download file not found.");
                return;
            }

            string extractFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sims4DLCs", Code);

            if (!System.IO.Directory.Exists(extractFolder))
            {
                System.IO.Directory.CreateDirectory(extractFolder);
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(downloadFilePath, extractFolder);
        } 

        public void Install(string gamepath, Logger logger) 
        {
            string extrackedFolder = System.IO.Path.Combine(_downloadFolder, Code);
            try
            {
                if (!System.IO.Directory.Exists(extrackedFolder))
                {
                    throw new InvalidOperationException("Extracted folder not found.");
                }

                string[] subfolders = System.IO.Directory.GetDirectories(extrackedFolder);                

                // Copy extracted files to the game path
                foreach (string file in System.IO.Directory.GetFiles(extrackedFolder, "*", System.IO.SearchOption.AllDirectories))
                {
                    string relativePath = System.IO.Path.GetRelativePath(extrackedFolder, file);
                    string destinationPath = System.IO.Path.Combine(gamepath, relativePath);
                    logger.AddLog($"Copying {file} to {destinationPath}");
                    // Ensure the destination directory exists
                    string? destinationDir = System.IO.Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDir) && !System.IO.Directory.Exists(destinationDir))
                    {
                        System.IO.Directory.CreateDirectory(destinationDir);
                    }
                    System.IO.File.Copy(file, destinationPath, true);
                }
            }
            catch
            {
                logger.AddLog("Failed to install DLC. Please clean the DLC folder and try again");
                logger.AddLog("Folder to clean: " + System.IO.Path.Combine(gamepath, Code));
            }
        }

        public void Remove(Logger logger)
        {
            string extrackedFolder = System.IO.Path.Combine(_downloadFolder, Code);
            string downloadedFilePath = System.IO.Path.Combine(_downloadFolder, $"{Code}.zip");
            try
            {
                System.IO.Directory.Delete(extrackedFolder, true);
                System.IO.File.Delete(downloadedFilePath);
            }
            catch
            {
                logger.AddLog("Failed to remove DLC. Please clean the Sims4DLCs folder in the temp directory after installing all dlcs");
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

    }
}
