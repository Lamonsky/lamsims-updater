using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace sims4_updater.Helpers
{
    public partial class StaticsVariables : ObservableObject
    {
        private static StaticsVariables? _instance;
        public static StaticsVariables Instance => _instance ??= new StaticsVariables();

        [ObservableProperty]
        public double progress = 0;

        [ObservableProperty]
        private string downloadSizeInfo = string.Empty;

        private StaticsVariables() { }
    }
}
