using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using sims4_updater.Helpers;
using System.IO;
using System.Linq;
using System.Runtime;

namespace sims4_updater.Models
{
    public partial class Sims4Model : ObservableObject
    {
        [ObservableProperty]
        private string _gamePath = String.Empty;

        private readonly Logger _logger;
        public Sims4AllDlcs AllDlcs { get; private set; } = new Sims4AllDlcs();

        
        public Sims4Model(Logger logger)
        {
            _logger = logger;

            string tempPath = ScanRegistry();

            if(!string.IsNullOrEmpty(tempPath))
            {
                GamePath = tempPath;
            }
            else
            {
                _logger.AddLog("Sims 4 installation path not found in registry.");
            }
        }

        private string ScanRegistry()
        {
            List<string> Registries = [
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Steam App 1222670",
                "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
                ];

            try
            {
                foreach(var regPath in Registries)
                {
                    using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(regPath))
                    {
                        if (key != null)
                        {
                            object? value = key.GetValue("InstallLocation");
                            if (value != null)
                            {
                                string path = value.ToString() ?? string.Empty;
                                _logger.AddLog($"Found Sims 4 installation path in registry: {path}");
                                return path;
                            }
                        }
                    }
                }            
            }
            catch (Exception ex)
            {
                _logger.AddLog($"Error while scanning registry: {ex.Message}");
            }
            _logger.AddLog("Sims 4 installation path not found in registry.");
            return string.Empty;
        }

        public void ScanDirectoryForDLCS(string path)
        {
            var files = Directory.GetFiles(path);

            var directories = Directory.GetDirectories(path);

            foreach (var dir in directories)
            {
                foreach (var dlc in AllDlcs.Sims4DLCs)
                {
                    if (dir.Contains(dlc.Code))
                    {
                        dlc.Installed = true;
                        dlc.ToInstall = true;
                        _logger.AddLog($"Found installed DLC: {dlc.Code} - {dlc.Name}");
                    }
                }
            }

            // Sortowanie: najpierw Installed (malejąco), potem Code (rosnąco)
            var sorted = AllDlcs.Sims4DLCs.OrderByDescending(d => d.Installed).ThenBy(d => d.Code).ToList();
            AllDlcs.Sims4DLCs.Clear();
            foreach (var dlc in sorted)
            {
                AllDlcs.Sims4DLCs.Add(dlc);
            }

            _logger.AddLog("Scan completed and list sorted.");
        }

    }
}
