using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace sims4_updater.Helpers
{
    public partial class Logger : ObservableObject
    {
        [ObservableProperty]
        private string _logs = string.Empty;

        private readonly object _lock = new object();

        public void AddLog(string log)
        {
            string CurrentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            lock (_lock)
            {
                Logs += $"{CurrentDateTime} - {log}{Environment.NewLine}";
            }
        }
    }
}
