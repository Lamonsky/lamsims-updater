using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace sims4_updater.Services
{
    public static class FileDownloader
    {
        public static async Task DownloadFileAsync(string url, string destinationPath)
        {
            // Implement the logic to download a file from the given URL and save it to the specified destination path.
            // You can use HttpClient or WebClient for this purpose.
            // Example using HttpClient:
            
            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(destinationPath, content);
            }
            
        }
    }
}
