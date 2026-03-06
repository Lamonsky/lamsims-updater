using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using sims4_updater.Helpers;
using sims4_updater.Services;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace sims4_updater.Models
{
    public static class Sims4DLCUnlocker
    {
        private const string UnlockerDllName = "version.dll";
        private const string ConfigFileName = "config.ini";
        private const string Sims4ConfigFileName = "g_The Sims 4.ini";
        private const string AppdataFolder = "anadius\\EA DLC Unlocker v2";

        public static async System.Threading.Tasks.Task InstallUnlocker(Logger logger)
        {
            try
            {
                logger.AddLog("=== Starting EA DLC Unlocker installation ===");

                // Check if version.dll exists in application folder
                string appFolder = AppDomain.CurrentDomain.BaseDirectory;
                string sourceDll = Path.Combine(appFolder, UnlockerDllName);

                // Detect EA app or Origin
                var (clientType, clientPath) = DetectEAClient(logger);
                if (string.IsNullOrEmpty(clientPath))
                {
                    logger.AddLog("ERROR: EA app or Origin not found!");
                    logger.AddLog("Make sure EA app or Origin is installed.");
                    return;
                }

                logger.AddLog($"Detected: {clientType}");
                logger.AddLog($"Path: {clientPath}");

                string dllUrlToDownload = String.Empty;

                if (clientType == "EA app")
                {
                    dllUrlToDownload = "https://github.com/Lamonsky/lamsims-updater/releases/download/Beta/ea_app_version.dll";
                }
                else if (clientType == "Origin")
                {
                    dllUrlToDownload = "https://github.com/Lamonsky/lamsims-updater/releases/download/Beta/origin_version.dll";
                }
                else
                {
                    logger.AddLog("ERROR: Unsupported client type detected!");
                    return;
                }

                if (File.Exists(sourceDll))
                {
                    logger.AddLog($"File {UnlockerDllName} already exists. Please remove it before installing or move updater to a different folder.");
                    return;
                }

                if (!File.Exists(sourceDll))
                {
                    logger.AddLog($"File {UnlockerDllName} not found in application folder. Attempting to download");
                   
                    await FileDownloader.DownloadFileAsync(dllUrlToDownload, sourceDll);

                }

                // Check admin rights
                if (!IsAdministrator())
                {
                    logger.AddLog("WARNING: No administrator privileges!");
                    logger.AddLog("Some operations may fail.");
                    logger.AddLog("Recommended to run as administrator.");
                }

                // Stop EA/Origin processes
                StopEAProcesses(logger, clientType);

                // Create config directories
                string configPath = CreateNeccesaryDirectories(logger);

                // Create config files
                CreateOrModifyNeccesaryFiles(logger, configPath);

                // Remove old unlocker versions
                RemoveOldUnlocker(logger, clientPath);


                // Copy version.dll to EA app/Origin
                string targetDll = Path.Combine(clientPath, UnlockerDllName);
                CopyDllFile(logger, sourceDll, targetDll);

                // Handle StagedEADesktop folder (for EA app updates)
                if (clientType == "EA app")
                {
                    HandleStagedFolder(logger, clientPath, sourceDll);
                    ModifyMachineIni(logger);
                    AddTaskToScheduler(logger, targetDll, clientPath);
                }

                // Remove from autostart
                RemoveFromAutostart(logger);

                logger.AddLog("=== Installation completed successfully! ===");
                logger.AddLog("You can now launch EA app/Origin and the game.");
            }
            catch (Exception ex)
            {
                logger.AddLog($"CRITICAL ERROR during installation: {ex.Message}");
                logger.AddLog($"Stack trace: {ex.StackTrace}");
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static (string ClientType, string ClientPath) DetectEAClient(Logger logger)
        {
            logger.AddLog("Detecting EA app/Origin...");

            // Try EA app first
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Desktop"))
                {
                    if (key != null)
                    {
                        string? clientPath = key.GetValue("ClientPath") as string;
                        if (!string.IsNullOrEmpty(clientPath) && Directory.Exists(Path.GetDirectoryName(clientPath)))
                        {
                            string parentPath = Directory.GetParent(clientPath)?.FullName ?? "";
                            if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                            {
                                return ("EA app", parentPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"Cannot read EA app registry: {ex.Message}");
            }

            // Try Origin (64-bit registry)
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Origin"))
                {
                    if (key != null)
                    {
                        string? clientPath = key.GetValue("ClientPath") as string;
                        if (!string.IsNullOrEmpty(clientPath) && Directory.Exists(Path.GetDirectoryName(clientPath)))
                        {
                            string parentPath = Directory.GetParent(clientPath)?.FullName ?? "";
                            if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                            {
                                return ("Origin", parentPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"Cannot read Origin registry (WOW6432Node): {ex.Message}");
            }

            // Try Origin (32-bit registry)
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Origin"))
                {
                    if (key != null)
                    {
                        string? clientPath = key.GetValue("ClientPath") as string;
                        if (!string.IsNullOrEmpty(clientPath) && Directory.Exists(Path.GetDirectoryName(clientPath)))
                        {
                            string parentPath = Directory.GetParent(clientPath)?.FullName ?? "";
                            if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                            {
                                return ("Origin", parentPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"Cannot read Origin registry: {ex.Message}");
            }

            return ("", "");
        }

        private static void StopEAProcesses(Logger logger, string clientType)
        {
            logger.AddLog($"Stopping {clientType} processes...");

            string processPattern = clientType == "EA app" ? "EA" : "Origin";

            try
            {
                Process[] processes = Process.GetProcesses()
                    .Where(p => p.ProcessName.StartsWith(processPattern, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (processes.Length == 0)
                {
                    logger.AddLog($"No running {processPattern}* processes found");
                    return;
                }

                foreach (Process process in processes)
                {
                    try
                    {
                        logger.AddLog($"Stopping: {process.ProcessName} (PID: {process.Id})");
                        process.Kill();
                        process.WaitForExit(5000);
                        logger.AddLog($"✓ Stopped: {process.ProcessName}");
                    }
                    catch (Exception ex)
                    {
                        logger.AddLog($"Cannot stop {process.ProcessName}: {ex.Message}");
                    }
                }

                System.Threading.Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                logger.AddLog($"Error stopping processes: {ex.Message}");
            }
        }

        private static void RemoveOldUnlocker(Logger logger, string clientPath)
        {
            logger.AddLog("Removing old unlocker versions...");

            string[] oldFiles = new[]
            {
                "version_o.dll",
                "winhttp.dll",
                "winhttp_o.dll"
            };

            foreach (string fileName in oldFiles)
            {
                string filePath = Path.Combine(clientPath, fileName);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        logger.AddLog($"✓ Removed: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    logger.AddLog($"Cannot remove {fileName}: {ex.Message}");
                }
            }

            // Remove old w_*.ini files
            try
            {
                string[] oldIniFiles = Directory.GetFiles(clientPath, "w_*.ini");
                foreach (string iniFile in oldIniFiles)
                {
                    try
                    {
                        File.Delete(iniFile);
                        logger.AddLog($"✓ Removed: {Path.GetFileName(iniFile)}");
                    }
                    catch (Exception ex)
                    {
                        logger.AddLog($"Cannot remove {Path.GetFileName(iniFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"Error removing old .ini files: {ex.Message}");
            }
        }

        private static void CopyDllFile(Logger logger, string source, string destination)
        {
            logger.AddLog($"Copying {UnlockerDllName}...");
            logger.AddLog($"From: {source}");
            logger.AddLog($"To: {destination}");

            try
            {
                File.Copy(source, destination, true);
                logger.AddLog($"✓ File {UnlockerDllName} copied successfully!");
            }
            catch (UnauthorizedAccessException)
            {
                logger.AddLog($"ERROR: No write permissions in {Path.GetDirectoryName(destination)}");
                logger.AddLog("Run application as administrator!");
            }
            catch (Exception ex)
            {
                logger.AddLog($"ERROR during copying: {ex.Message}");
            }
        }

        private static void HandleStagedFolder(Logger logger, string clientPath, string sourceDll)
        {
            logger.AddLog("Handling StagedEADesktop folder...");

            try
            {
                string? parentFolder = Directory.GetParent(clientPath)?.FullName;
                if (string.IsNullOrEmpty(parentFolder))
                {
                    logger.AddLog("Cannot determine parent folder.");
                    return;
                }

                string stagedFolder = Path.Combine(parentFolder, "StagedEADesktop");
                string targetFolder = Path.Combine(stagedFolder, "EA Desktop");

                // Create folders
                if (!Directory.Exists(stagedFolder))
                {
                    Directory.CreateDirectory(stagedFolder);
                    logger.AddLog($"✓ Created: {stagedFolder}");
                }

                if (!Directory.Exists(targetFolder))
                {
                    Directory.CreateDirectory(targetFolder);
                    logger.AddLog($"✓ Created: {targetFolder}");
                }

                // Copy version.dll to StagedEADesktop
                string stagedDll = Path.Combine(targetFolder, UnlockerDllName);
                File.Copy(sourceDll, stagedDll, true);
                logger.AddLog($"✓ Copied {UnlockerDllName} to StagedEADesktop");
            }
            catch (Exception ex)
            {
                logger.AddLog($"Error handling StagedEADesktop: {ex.Message}");
            }
        }

        private static void ModifyMachineIni(Logger logger)
        {
            logger.AddLog("Modifying machine.ini...");

            try
            {
                string machineIniPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "EA Desktop",
                    "machine.ini"
                );

                if (!File.Exists(machineIniPath))
                {
                    logger.AddLog($"File machine.ini does not exist: {machineIniPath}");
                    return;
                }

                string content = File.ReadAllText(machineIniPath);
                if (!content.Contains("machine.bgsstandaloneenabled=0"))
                {
                    File.AppendAllText(machineIniPath, "\nmachine.bgsstandaloneenabled=0\n");
                    logger.AddLog("✓ Updated machine.ini");
                }
                else
                {
                    logger.AddLog("machine.ini already contains required configuration");
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"Cannot modify machine.ini: {ex.Message}");
                logger.AddLog("This is not critical, can continue.");
            }
        }

        private static void RemoveFromAutostart(Logger logger)
        {
            logger.AddLog("Removing EADM from autostart...");
            string appName = "EADM";

            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (key.GetValue(appName) != null)
                        {
                            key.DeleteValue(appName);
                            logger.AddLog($"✓ Removed {appName} from autostart");
                        }
                        else
                        {
                            logger.AddLog("EADM was not in autostart");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"Error removing from autostart: {ex.Message}");
            }
        }

        private static string CreateNeccesaryDirectories(Logger logger)
        {
            logger.AddLog("Creating configuration folders...");

            string roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string eadlcunlockerpath = Path.Combine(roamingPath, AppdataFolder);

            try
            {
                if (!Directory.Exists(eadlcunlockerpath))
                {
                    Directory.CreateDirectory(eadlcunlockerpath);
                    logger.AddLog($"✓ Created: {eadlcunlockerpath}");
                }
                else
                {
                    logger.AddLog($"Folder already exists: {eadlcunlockerpath}");
                }
            }
            catch (Exception ex)
            {
                logger.AddLog($"ERROR creating folder: {ex.Message}");
                throw;
            }

            return eadlcunlockerpath;
        }
        
        private static void CreateOrModifyNeccesaryFiles(Logger logger, string eadlcunlockerpath)
        {
            logger.AddLog("Creating configuration files...");

            try
            {
                string configFilePath = Path.Combine(eadlcunlockerpath, ConfigFileName);
                File.WriteAllText(configFilePath, ConfigFile);
                logger.AddLog($"✓ Created: {ConfigFileName}");

                string sims4ConfigFilePath = Path.Combine(eadlcunlockerpath, Sims4ConfigFileName);
                File.WriteAllText(sims4ConfigFilePath, Sims4ConfigFile);
                logger.AddLog($"✓ Created: {Sims4ConfigFileName}");
            }
            catch (Exception ex)
            {
                logger.AddLog($"ERROR creating configuration files: {ex.Message}");
                throw;
            }
        }

        private static string GetEAAppPathFromUser(Logger logger)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Origin or EA Desktop folder for eg. C:\\Program Files (x86)\\Electronic Arts\\EA Desktop\\EA Desktop"                
            };
            if (dialog.ShowDialog() == true)
            {
                string selectedPath = dialog.FolderName;
                logger.AddLog($"Selected EA app path: {selectedPath}");
                return dialog.FolderName;
            }
            else
            {
                logger.AddLog("No folder selected.");
                return string.Empty;
            }
            
        }

        private static void CheckAndCreateStagedFolders(Logger logger, string eaapppath)
        {
            string? parentFolder = Directory.GetParent(eaapppath)?.FullName;
            if (string.IsNullOrEmpty(parentFolder))
            {
                logger.AddLog("Error: Cannot determine parent folder of EA app path.");
                return;
            }
            string stagedFolder = Path.Combine(parentFolder, "StagedEADesktop");
            string targetFolder = Path.Combine(stagedFolder, "EA Desktop");
            logger.AddLog($"Checking and creating folders: {stagedFolder} and {targetFolder}");
            if (!Directory.Exists(stagedFolder))
            {
                Directory.CreateDirectory(stagedFolder);
                logger.AddLog($"Created folder: {stagedFolder}");
            }
            else
            {
                logger.AddLog($"Folder already exists: {stagedFolder}");
            }
            if (!Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
                logger.AddLog($"Created folder: {targetFolder}");
            }
            else
            {
                logger.AddLog($"Folder already exists: {targetFolder}");
            }
        }

        private static void AddTaskToScheduler(Logger logger, string sourceDll, string clientPath)
        {
            logger.AddLog("Creating scheduled task (Task Scheduler)...");

            try
            {
                string taskName = "copy_dlc_unlocker";
                string? parentFolder = Directory.GetParent(clientPath)?.FullName;
                if (string.IsNullOrEmpty(parentFolder))
                {
                    logger.AddLog("Cannot determine parent folder.");
                    return;
                }

                string targetFolder = Path.Combine(parentFolder, "StagedEADesktop", "EA Desktop");

                using (TaskService ts = new TaskService())
                {
                    // Remove old task if exists
                    try
                    {
                        ts.RootFolder.DeleteTask(taskName, false);
                    }
                    catch { }

                    TaskDefinition td = ts.NewTask();
                    td.RegistrationInfo.Description = "Automatycznie kopiuje EA DLC Unlocker do StagedEADesktop po aktualizacji EA app";
                    td.RegistrationInfo.Author = "Sims 4 Updater";

                    td.Principal.RunLevel = TaskRunLevel.Highest;
                    td.Principal.LogonType = TaskLogonType.InteractiveToken;

                    // Dummy trigger (task will be run manually)
                    TimeTrigger trigger = new TimeTrigger
                    {
                        StartBoundary = new DateTime(2000, 1, 1, 0, 0, 0),
                        Enabled = false
                    };
                    td.Triggers.Add(trigger);

                    // Action to copy DLL
                    ExecAction action = new ExecAction(
                        "xcopy.exe",
                        $"/Y \"{sourceDll}\" \"{targetFolder}\\*\"",
                        null
                    );
                    td.Actions.Add(action);

                    td.Settings.AllowDemandStart = true;
                    td.Settings.DisallowStartIfOnBatteries = false;
                    td.Settings.StopIfGoingOnBatteries = false;
                    td.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(5);

                    Microsoft.Win32.TaskScheduler.Task registeredTask = ts.RootFolder.RegisterTaskDefinition(
                        taskName,
                        td,
                        TaskCreation.CreateOrUpdate,
                        null,
                        null,
                        TaskLogonType.InteractiveToken
                    );

                    logger.AddLog($"✓ Task '{taskName}' created successfully");

                    // Run task immediately
                    registeredTask.Run();
                    logger.AddLog("✓ Task started immediately");
                }
            }
            catch (UnauthorizedAccessException)
            {
                logger.AddLog("ERROR: No permissions to create tasks.");
                logger.AddLog("Run application as administrator!");
            }
            catch (Exception ex)
            {
                logger.AddLog($"Error creating task: {ex.Message}");
            }
        }

        private static string ConfigFile = """
            [config]
            ; By default Unlocker is loaded every time you start Origin/EA Desktop
            ; and is disabled if you add "-disableUnlocker" to the shortcut target.
            ; Set to 1 to load the Unlocker only when you add "-enableUnlocker"
            ; to the shortcut target. TARGET MUST BE Origin.exe OR EADesktop.exe!
            defaultDisabled=0

            ; log the LSX traffic
            logLSX=0

            ; show message when Unlocker is working
            showMessages=0

            ; logs more info
            debugMode=0

            ; replace DLCs instead of adding the pirated ones to the legit ones
            ; this is/was needed for some games when running through EA app
            replaceDLCs=0

            ; needed to unlock full game in It Takes Two Friend's Pass
            ; disable if you play some trial and it crashes
            fakeFullGame=1

            ; some games (like The Sims 4) check if your game language
            ; is among the languages you're allowed to use
            ; these are all languages I could find that EA uses
            ; if it breaks some game for you - set it to the same languages
            ; as in __Installer/installerdata.xml file
            languages=ar_SA,cs_CZ,da_DK,de_DE,el_GR,en_AU,en_CA,en_GB,en_US,es_ES,es_MX,fi_FI,fr_CA,fr_FR,hu_HU,it_IT,ja_JP,ko_KR,nl_NL,no_NO,pl_PL,pt_BR,pt_PT,ru_RU,sv_SE,th_TH,tr_TR,zh_CN,zh_HK,zh_TW

            [autoupdate]
            ; unused
            CNT=0
            """;

        private static string Sims4ConfigFile = """
            ; if you want to disable a DLC - add ; before IID

            [config]
            CNT=147

            NAM1=Life of the Party Digital Content
            IID1=SIMS4.OFF.SOLP.0x0000000000008E14
            ETG1=LifeOfTheParty_0x0000000000008E14:36372
            GRP1=THESIMS4PC
            TYP1=DEFAULT

            NAM2=Up All Night Digital Content
            IID2=SIMS4.OFF.SOLP.0x0000000000009AED
            ETG2=UpAllNight_0x0000000000009AED:39661
            GRP2=THESIMS4PC
            TYP2=DEFAULT

            NAM3=Digital Deluxe Upgrade, unlocks the stuff above, included for completion
            IID3=SIMS4.OFF.SOLP.0x000000000000AB5C
            ETG3=DigitalDeluxeUpgrade_0x000000000000AB5C:43868
            GRP3=THESIMS4PC
            TYP3=DEFAULT

            NAM4=P.R.I.M.A. 3000 computer (Collector's Edition Exclusive)
            IID4=SIMS4.OFF.SOLP.0x0000000000007BC5
            ETG4=Prima_0x0000000000007BC5:31685
            GRP4=THESIMS4PC
            TYP4=DEFAULT

            NAM5=Grim's Ghoulish Guitar (email subscription bonus)
            IID5=SIMS4.OFF.SOLP.0x000000000002376E
            ETG5=Incentive01_musicGuitar_EF0GENreaper:145262
            GRP5=THESIMS4PC
            TYP5=DEFAULT

            NAM6=The Family Portrait (The Sims Mobile bonus)
            IID6=SIMS4.OFF.SOLP.0x000000000002AEA7
            ETG6=ONLINE_ACCESS
            GRP6=THESIMS4PC
            TYP6=DEFAULT

            NAM7=Let There Be Plumbobs Award (TS3)
            IID7=OFB-EAST:55107
            ETG7=ONLINE_ACCESS
            GRP7=THESIMS4PC
            TYP7=DEFAULT

            NAM8=Never Ending World Adventures Award (TS3 EP01)
            IID8=DR:137311600
            ETG8=ONLINE_ACCESS
            GRP8=THESIMS4PC
            TYP8=DEFAULT

            NAM9=Shine On Ambitions Award (TS3 EP02)
            IID9=DR:169598400
            ETG9=ONLINE_ACCESS
            GRP9=THESIMS4PC
            TYP9=DEFAULT

            NAM10=UV Light Late Night Award (TS3 EP03)
            IID10=DR:194011300
            ETG10=ONLINE_ACCESS
            GRP10=THESIMS4PC
            TYP10=DEFAULT

            NAM11=Beacon of Ages Generations Award (TS3 EP04)
            IID11=DR:219720800
            ETG11=ONLINE_ACCESS
            GRP11=THESIMS4PC
            TYP11=DEFAULT

            NAM12=Paws Anonymous Pets Award (TS3 EP05)
            IID12=DR:234872200
            ETG12=ONLINE_ACCESS
            GRP12=THESIMS4PC
            TYP12=DEFAULT

            NAM13=Your Name in Lights Showtime Award (TS3 EP06)
            IID13=OFB-EAST:42334
            ETG13=ONLINE_ACCESS
            GRP13=THESIMS4PC
            TYP13=DEFAULT

            NAM14=Enchanted Aurora Supernatural Award (TS3 EP07)
            IID14=OFB-EAST:49019
            ETG14=ONLINE_ACCESS
            GRP14=THESIMS4PC
            TYP14=DEFAULT

            NAM15=Whatever the Weather Seasons Award (TS3 EP08)
            IID15=OFB-EAST:49463
            ETG15=ONLINE_ACCESS
            GRP15=THESIMS4PC
            TYP15=DEFAULT

            NAM16=Alma Mater University Life Award (TS3 EP09)
            IID16=OFB-EAST:109545788
            ETG16=ONLINE_ACCESS
            GRP16=THESIMS4PC
            TYP16=DEFAULT

            NAM17=Tiki Topper Island Paradise Award (TS3 EP10)
            IID17=OFB-EAST:109546946
            ETG17=ONLINE_ACCESS
            GRP17=THESIMS4PC
            TYP17=DEFAULT

            NAM18=Time Travelers Into the Future Award (TS3 EP11)
            IID18=OFB-EAST:64320
            ETG18=ONLINE_ACCESS
            GRP18=THESIMS4PC
            TYP18=DEFAULT

            NAM19=The Sims™ 4 Holiday Celebration Pack
            IID19=SIMS4.OFF.SOLP.0x000000000001266C
            ETG19=Sims4HolidayObjectsBundle_0x000000000001266C:75372
            GRP19=THESIMS4PC
            TYP19=DEFAULT

            NAM20=The Sims™ 4 Outdoor Retreat
            IID20=SIMS4.OFF.SOLP.0x0000000000011A4B
            ETG20=GreatOutdoors_0x0000000000011A4B:72267
            GRP20=THESIMS4PC
            TYP20=DEFAULT

            NAM21=The Sims™ 4 Get to Work
            IID21=SIMS4.OFF.SOLP.0x0000000000011AC5
            ETG21=ExpansionPack1_0x0000000000011AC5:72389
            GRP21=THESIMS4PC
            TYP21=DEFAULT

            NAM22=The Sims™ 4 Luxury Party Stuff
            IID22=SIMS4.OFF.SOLP.0x0000000000016390
            ETG22=SP01_LuxuryPartyStuff_0x0000000000016390:91024
            GRP22=THESIMS4PC
            TYP22=DEFAULT

            NAM23=The Sims™ 4 Perfect Patio Stuff
            IID23=SIMS4.OFF.SOLP.0x0000000000016B37
            ETG23=SP02_PerfectPatioStuff_0x0000000000016B37:92983
            GRP23=THESIMS4PC
            TYP23=DEFAULT

            NAM24=The Sims™ 4 Spa Day
            IID24=SIMS4.OFF.SOLP.0x0000000000016C2A
            ETG24=GP02_SpaDay_0x0000000000016C2A:93226
            GRP24=THESIMS4PC
            TYP24=DEFAULT

            NAM25=The Sims™ 4 Cool Kitchen Stuff
            IID25=SIMS4.OFF.SOLP.0x0000000000018B66
            ETG25=SP03_CoolKitchenStuff_0x0000000000018B66:101222
            GRP25=THESIMS4PC
            TYP25=DEFAULT

            NAM26=The Sims™ 4 Spooky Stuff
            IID26=SIMS4.OFF.SOLP.0x0000000000019B59
            ETG26=SP04_SpookyStuff_0x0000000000019B59:105305
            GRP26=THESIMS4PC
            TYP26=DEFAULT

            NAM27=The Sims™ 4 Get Together
            IID27=SIMS4.OFF.SOLP.0x00000000000170FF
            ETG27=GetTogether_0x00000000000170FF:94463
            GRP27=THESIMS4PC
            TYP27=DEFAULT

            NAM28=The Sims™ 4 Movie Hangout Stuff
            IID28=SIMS4.OFF.SOLP.0x000000000001A9A7
            ETG28=SP05_MovieNight_0x000000000001A9A7:108967
            GRP28=THESIMS4PC
            TYP28=DEFAULT

            NAM29=The Sims™ 4 Romantic Garden Stuff
            IID29=SIMS4.OFF.SOLP.0x000000000001C04E
            ETG29=SP06_SecretGarden_0x000000000001C04E:114766
            GRP29=THESIMS4PC
            TYP29=DEFAULT

            NAM30=The Sims™ 4 Dine Out
            IID30=SIMS4.OFF.SOLP.0x000000000001C03A
            ETG30=GP03_Restaurants_0x000000000001C03A:114746
            GRP30=THESIMS4PC
            TYP30=DEFAULT

            NAM31=The Sims™ 4 Kids Room Stuff
            IID31=SIMS4.OFF.SOLP.0x000000000001D5F2
            ETG31=SP07_KidsRoom_0x000000000001D5F2:120306
            GRP31=THESIMS4PC
            TYP31=DEFAULT

            NAM32=The Sims™ 4 Backyard Stuff
            IID32=SIMS4.OFF.SOLP.0x0000000000020176
            ETG32=SP08_BackyardFun_0x0000000000020176:131446
            GRP32=THESIMS4PC
            TYP32=DEFAULT

            NAM33=The Sims™ 4 City Living
            IID33=SIMS4.OFF.SOLP.0x000000000001D5ED
            ETG33=EP03_CityLife_0x000000000001D5ED:120301
            GRP33=THESIMS4PC
            TYP33=DEFAULT

            NAM34=The Sims™ 4 Vintage Glamour Stuff
            IID34=SIMS4.OFF.SOLP.0x0000000000022C32
            ETG34=SP09_GlamorousStuffPack_0x0000000000022C32:142386
            GRP34=THESIMS4PC
            TYP34=DEFAULT

            NAM35=The Sims™ 4 Vampires
            IID35=SIMS4.OFF.SOLP.0x000000000002376D
            ETG35=GP04_Vampires_0x000000000002376D:145261
            GRP35=THESIMS4PC
            TYP35=DEFAULT

            NAM36=The Sims™ 4 Bowling Night Stuff
            IID36=SIMS4.OFF.SOLP.0x0000000000027128
            ETG36=SP10_BowlingStuff_0x0000000000027128:160040
            GRP36=THESIMS4PC
            TYP36=DEFAULT

            NAM37=The Sims™ 4 Parenthood
            IID37=SIMS4.OFF.SOLP.0x0000000000027890
            ETG37=GP05_FamilyFirst_0x0000000000027890:161936
            GRP37=THESIMS4PC
            TYP37=DEFAULT

            NAM38=The Sims™ 4 Fitness Stuff
            IID38=SIMS4.OFF.SOLP.0x0000000000028FE2
            ETG38=SP11_FitnessStuff_0x0000000000028FE2:167906
            GRP38=THESIMS4PC
            TYP38=DEFAULT

            NAM39=The Sims™ 4 Toddler Stuff
            IID39=SIMS4.OFF.SOLP.0x000000000002A4FE
            ETG39=SP12_ToddlerStuff_0x000000000002A4FE:173310
            GRP39=THESIMS4PC
            TYP39=DEFAULT

            NAM40=The Sims™ 4 Cats & Dogs
            IID40=SIMS4.OFF.SOLP.0x000000000002714B
            ETG40=EP04_CatsandDogs_0x000000000002714B:160075
            GRP40=THESIMS4PC
            TYP40=DEFAULT

            NAM41=The Sims™ 4 Laundry Day Stuff
            IID41=SIMS4.OFF.SOLP.0x000000000002CA06
            ETG41=SP13_LaundryStuff_0x000000000002CA06:182790
            GRP41=THESIMS4PC
            TYP41=DEFAULT

            NAM42=The Sims™ 4 Jungle Adventure
            IID42=SIMS4.OFF.SOLP.0x000000000002B073
            ETG42=GP06_JungleWorld_0x000000000002B073:176243
            GRP42=THESIMS4PC
            TYP42=DEFAULT

            NAM43=The Sims™ 4 My First Pet Stuff
            IID43=SIMS4.OFF.SOLP.0x000000000002EA24
            ETG43=SP14_MyFirstPetStuff_0x000000000002EA24:191012
            GRP43=THESIMS4PC
            TYP43=DEFAULT

            NAM44=The Sims™ 4 Seasons
            IID44=SIMS4.OFF.SOLP.0x000000000002E2C7
            ETG44=EP05_Seasons_0x000000000002E2C7:189127
            GRP44=THESIMS4PC
            TYP44=DEFAULT

            NAM45=The Sims™ 4 Get Famous
            IID45=SIMS4.OFF.SOLP.0x0000000000030553
            ETG45=EP06_GetFamous_0x0000000000030553:197971
            GRP45=THESIMS4PC
            TYP45=DEFAULT

            NAM46=The Sims™ 4 StrangerVille
            IID46=SIMS4.OFF.SOLP.0x0000000000033910
            ETG46=GP07_StrangerVille_0x0000000000033910:211216
            GRP46=THESIMS4PC
            TYP46=DEFAULT

            NAM47=The Sims™ 4 Island Living
            IID47=SIMS4.OFF.SOLP.0x00000000000327AF
            ETG47=EP07_IslandLiving_0x00000000000327AF:206767
            GRP47=THESIMS4PC
            TYP47=DEFAULT

            NAM48=The Sims™ 4 Moschino Stuff Pack
            IID48=SIMS4.OFF.SOLP.0x000000000003749F
            ETG48=SP15_MoschinoStuff_0x000000000003749F:226463
            GRP48=THESIMS4PC
            TYP48=DEFAULT

            NAM49=The Sims™ 4 Realm of Magic
            IID49=SIMS4.OFF.SOLP.0x0000000000036EE8
            ETG49=GP08_RealmOfMagic_0x0000000000036EE8:225000
            GRP49=THESIMS4PC
            TYP49=DEFAULT

            NAM50=The Sims™ 4 Discover University
            IID50=SIMS4.OFF.SOLP.0x0000000000035113
            ETG50=EP08_DiscoverUniversity_0x0000000000035113:217363
            GRP50=THESIMS4PC
            TYP50=DEFAULT

            NAM51=The Sims™ 4 Tiny Living
            IID51=SIMS4.OFF.SOLP.0x000000000003A92D
            ETG51=SP16_TinyLivingStuff_0x000000000003A92D:239917
            GRP51=THESIMS4PC
            TYP51=DEFAULT

            NAM52=The Sims™ 4 Eco Lifestyle
            IID52=SIMS4.OFF.SOLP.0x0000000000039AA7
            ETG52=EP09_EcoLifestyle_0x0000000000039AA7:236199
            GRP52=THESIMS4PC
            TYP52=DEFAULT

            NAM53=The Sims™ 4 Nifty Knitting Stuff Pack
            IID53=SIMS4.OFF.SOLP.0x000000000003D4E9
            ETG53=SP17_NiftyKnittingStuff_0x000000000003D4E9:251113
            GRP53=THESIMS4PC
            TYP53=DEFAULT

            NAM54=The Sims™ 4 STAR WARS™: Journey to Batuu Game Pack
            IID54=SIMS4.OFF.SOLP.0x000000000003B1C1
            ETG54=GP09_JourneytoBatuu_0x000000000003B1C1:242113
            GRP54=THESIMS4PC
            TYP54=DEFAULT

            NAM55=The Sims™ 4 Snowy Escape
            IID55=SIMS4.OFF.SOLP.0x000000000003C344
            ETG55=EP10_SnowyEscape_0x000000000003C344:246596
            GRP55=THESIMS4PC
            TYP55=DEFAULT

            NAM56=The Sims™ 4 Paranormal Stuff Pack
            IID56=SIMS4.OFF.SOLP.0x000000000003FA50
            ETG56=SP18_ParanormalStuff_0x000000000003FA50:260688
            GRP56=THESIMS4PC
            TYP56=DEFAULT

            NAM57=The Sims™ 4 Throwback Fit Kit
            IID57=SIMS4.OFF.SOLP.0x000000000004008B
            ETG57=SP20_ThrowbackFitKit_0x000000000004008B:262283
            GRP57=THESIMS4PC
            TYP57=DEFAULT

            NAM58=The Sims™ 4 Country Kitchen Kit
            IID58=SIMS4.OFF.SOLP.0x000000000004008C
            ETG58=SP21_CountryKitchenKit_0x000000000004008C:262284
            GRP58=THESIMS4PC
            TYP58=DEFAULT

            NAM59=The Sims™ 4 Bust the Dust Kit
            IID59=SIMS4.OFF.SOLP.0x000000000004008D
            ETG59=SP22_BusttheDustKit_0x000000000004008D:262285
            GRP59=THESIMS4PC
            TYP59=DEFAULT

            NAM60=The Sims™ 4 Courtyard Oasis Kit
            IID60=SIMS4.OFF.SOLP.0x0000000000043896
            ETG60=SP23_CourtyardOasisKit_0x0000000000043896:276630
            GRP60=THESIMS4PC
            TYP60=DEFAULT

            NAM61=The Sims™ 4 Dream Home Decorator
            IID61=SIMS4.OFF.SOLP.0x0000000000040A01
            ETG61=GP10_DreamHomeDecorator_0x0000000000040A01:264705
            GRP61=THESIMS4PC
            TYP61=DEFAULT

            NAM62=The Sims™ 4 Cottage Living
            IID62=SIMS4.OFF.SOLP.0x00000000000405E6
            ETG62=EP11_CottageLiving_0x00000000000405E6:263654
            GRP62=THESIMS4PC
            TYP62=DEFAULT

            NAM63=The Sims™ 4 Cottage Living (bonuses only)
            IID63=SIMS4.OFF.SOLP.0x00000000000454E8
            ETG63=GnomeOnTheGo_CLPreorderBonus:283880
            GRP63=THESIMS4PC
            TYP63=DEFAULT

            NAM64=The Sims™ 4 Industrial Loft Kit
            IID64=SIMS4.OFF.SOLP.0x00000000000463B9
            ETG64=SP25_IndustrialLoftKit_0x00000000000463B9:287673
            GRP64=THESIMS4PC
            TYP64=DEFAULT

            NAM65=The Sims™ 4 Fashion Street Kit
            IID65=SIMS4.OFF.SOLP.0x0000000000045113
            ETG65=SP24_FashionStreetKit_0x0000000000045113:282899
            GRP65=THESIMS4PC
            TYP65=DEFAULT

            NAM66=The Sims™ 4 Incheon Arrivals Kit
            IID66=SIMS4.OFF.SOLP.0x00000000000467AA
            ETG66=SP26_IncheonArrivalsKit_0x00000000000467AA:288682
            GRP66=THESIMS4PC
            TYP66=DEFAULT

            NAM67=The Sims™ 4 Blooming Rooms Kit
            IID67=SIMS4.OFF.SOLP.0x0000000000046859
            ETG67=SP29_BloomingRoomsKit_0x0000000000046859:288857
            GRP67=THESIMS4PC
            TYP67=DEFAULT

            NAM68=The Sims™ 4 Modern Menswear Kit
            IID68=SIMS4.OFF.SOLP.0x0000000000046858
            ETG68=SP28_ModernMenswearKit_0x0000000000046858:288856
            GRP68=THESIMS4PC
            TYP68=DEFAULT

            NAM69=The Sims™ 4 Carnaval Streetwear Kit
            IID69=SIMS4.OFF.SOLP.0x000000000004685A
            ETG69=SP30_CarnavalKit_0x000000000004685A:288858
            GRP69=THESIMS4PC
            TYP69=DEFAULT

            NAM70=The Sims™ 4 My Wedding Stories
            IID70=SIMS4.OFF.SOLP.0x0000000000047D7E
            ETG70=GP11_WeddingStories_0x0000000000047D7E:294270
            GRP70=THESIMS4PC
            TYP70=DEFAULT

            NAM71=The Sims™ 4 Décor to the Max Kit
            IID71=SIMS4.OFF.SOLP.0x0000000000048CFD
            ETG71=SP31_Maximalism Kit_0x0000000000048CFD:298237
            GRP71=THESIMS4PC
            TYP71=DEFAULT

            NAM72=The Sims™ 4 Moonlight Chic Kit
            IID72=SIMS4.OFF.SOLP.0x000000000004CB7A
            ETG72=SP32_DateNightParisKit_0x000000000004CB7A:314234
            GRP72=THESIMS4PC
            TYP72=DEFAULT

            NAM73=The Sims™ 4 Little Campers Kit
            IID73=SIMS4.OFF.SOLP.0x000000000004CB7B
            ETG73=SP33_CozyCampoutKit_0x000000000004CB7B:314235
            GRP73=THESIMS4PC
            TYP73=DEFAULT

            NAM74=The Sims™ 4 Werewolves
            IID74=SIMS4.OFF.SOLP.0x0000000000048E3E
            ETG74=GP12_Werewolves_0x0000000000048E3E:298558
            GRP74=THESIMS4PC
            TYP74=DEFAULT

            NAM75=The Sims™ 4 High School Years (normal)
            IID75=SIMS4.OFF.SOLP.0x0000000000047D4B
            ETG75=EP12_HighSchool_0x0000000000047D4B:294219
            GRP75=THESIMS4PC
            TYP75=DEFAULT

            NAM76=The Sims™ 4 High School Years (preorder)
            IID76=SIMS4.OFF.SOLP.0x000000000004AAD0
            ETG76=EP12_PrePurchase_0x000000000004AAD0:305872
            GRP76=THESIMS4PC
            TYP76=DEFAULT

            NAM77=The Sims™ 4 High School Years (bonuses only)
            IID77=SIMS4.OFF.SOLP.0x000000000004AAD1
            ETG77=EP12_IncentiveItems_0x000000000004AAD1:305873
            GRP77=THESIMS4PC
            TYP77=DEFAULT

            NAM78=The Sims™ 4 First Fits Kit
            IID78=SIMS4.OFF.SOLP.0x000000000004E9E7
            ETG78=SP34_LittleTrendsetters_0x000000000004E9E7:322023
            GRP78=THESIMS4PC
            TYP78=DEFAULT

            NAM79=The Sims™ 4 Desert Luxe Kit
            IID79=SIMS4.OFF.SOLP.0x000000000004E9E8
            ETG79=SP35_DesertLuxury_0x000000000004E9E8:322024
            GRP79=THESIMS4PC
            TYP79=DEFAULT

            NAM80=The Sims™ 4 Pastel Pop Kit
            IID80=SIMS4.OFF.SOLP.0x0000000000050752
            ETG80=SP36_PastelPopKit_0x0000000000050752:329554
            GRP80=THESIMS4PC
            TYP80=DEFAULT

            NAM81=The Sims™ 4 Everyday Clutter Kit
            IID81=SIMS4.OFF.SOLP.0x0000000000050753
            ETG81=SP37_EverydayClutterKit_0x0000000000050753:329555
            GRP81=THESIMS4PC
            TYP81=DEFAULT

            NAM82=The Sims™ 4 Simtimates Collection Kit
            IID82=SIMS4.OFF.SOLP.0x0000000000052633
            ETG82=SP38_ModernLoungewearKit_0x0000000000052633:337459
            GRP82=THESIMS4PC
            TYP82=DEFAULT

            NAM83=The Sims™ 4 Bathroom Clutter Kit
            IID83=SIMS4.OFF.SOLP.0x0000000000052C6F
            ETG83=SP39_BathroomClutterKit_0x0000000000052C6F:339055
            GRP83=THESIMS4PC
            TYP83=DEFAULT

            NAM84=The Sims™ 4 Growing Together (normal)
            IID84=SIMS4.OFF.SOLP.0x000000000004DBB0
            ETG84=EP13_SeasonsOfLife_0x000000000004DBB0:318384
            GRP84=THESIMS4PC
            TYP84=DEFAULT

            NAM85=The Sims™ 4 Growing Together (preorder)
            IID85=SIMS4.OFF.SOLP.0x00000000000530C0
            ETG85=EP13_PrePurchase_0x00000000000530C0:340160
            GRP85=THESIMS4PC
            TYP85=DEFAULT

            NAM86=The Sims™ 4 Growing Together (bonuses only)
            IID86=SIMS4.OFF.SOLP.0x00000000000530BF
            ETG86=EP13_IncentiveItems_0x00000000000530BF:340159
            GRP86=THESIMS4PC
            TYP86=DEFAULT

            NAM87=The Sims™ 4 Greenhouse Haven Kit
            IID87=SIMS4.OFF.SOLP.0x0000000000056640
            ETG87=SP40_GreenhouseKit_0x0000000000056640:353856
            GRP87=THESIMS4PC
            TYP87=DEFAULT

            NAM88=The Sims™ 4 Basement Treasures Kit
            IID88=SIMS4.OFF.SOLP.0x0000000000056641
            ETG88=SP41_GrungyBasementKit_0x0000000000056641:353857
            GRP88=THESIMS4PC
            TYP88=DEFAULT

            NAM89=The Sims™ 4 Grunge Revival Kit
            IID89=SIMS4.OFF.SOLP.0x00000000000574B3
            ETG89=SP42_VintageGrungeKit_0x00000000000574B3:357555
            GRP89=THESIMS4PC
            TYP89=DEFAULT

            NAM90=The Sims™ 4 Book Nook Kit
            IID90=SIMS4.OFF.SOLP.0x00000000000574B4
            ETG90=SP43_BookNookKit_0x00000000000574B4:357556
            GRP90=THESIMS4PC
            TYP90=DEFAULT

            NAM91=The Sims™ 4 Horse Ranch (normal)
            IID91=SIMS4.OFF.SOLP.0x000000000004F3B5
            ETG91=EP14_HorseRanch_0x000000000004F3B5:324533
            GRP91=THESIMS4PC
            TYP91=DEFAULT

            NAM92=The Sims™ 4 Horse Ranch (preorder)
            IID92=SIMS4.OFF.SOLP.0x0000000000057E0A
            ETG92=EP14_PrePurchase_0x0000000000057E0A:359946
            GRP92=THESIMS4PC
            TYP92=DEFAULT

            NAM93=The Sims™ 4 Horse Ranch (bonuses only)
            IID93=SIMS4.OFF.SOLP.0x0000000000057E09
            ETG93=EP14_IncentiveItems_0x0000000000057E09:359945
            GRP93=THESIMS4PC
            TYP93=DEFAULT

            NAM94=The Sims™ 4 Poolside Splash Kit
            IID94=SIMS4.OFF.SOLP.0x000000000005A5A4
            ETG94=SP44_PoolsideSplash_0x000000000005A5A4:370084
            GRP94=THESIMS4PC
            TYP94=DEFAULT

            NAM95=The Sims™ 4 Modern Luxe Kit
            IID95=SIMS4.OFF.SOLP.0x000000000005A5A5
            ETG95=SP45_ModernLuxe_0x000000000005A5A5:370085
            GRP95=THESIMS4PC
            TYP95=DEFAULT

            NAM96=The Sims™ 4 Home Chef Hustle Stuff Pack
            IID96=SIMS4.OFF.SOLP.0x0000000000058A09
            ETG96=SP46_HomeChefHustles_0x0000000000058A09:363017
            GRP96=THESIMS4PC
            TYP96=DEFAULT

            NAM97=The Sims™ 4 For Rent (normal)
            IID97=SIMS4.OFF.SOLP.0x000000000005A8CE
            ETG97=EP15_ForRent_0x000000000005A8CE:370894
            GRP97=THESIMS4PC
            TYP97=DEFAULT

            NAM98=The Sims™ 4 For Rent (preorder)
            IID98=SIMS4.OFF.SOLP.0x000000000005CD3D
            ETG98=EP15_PrePurchase_0x000000000005CD3D:380221
            GRP98=THESIMS4PC
            TYP98=DEFAULT

            NAM99=The Sims™ 4 For Rent (bonuses only)
            IID99=SIMS4.OFF.SOLP.0x000000000005CD3C
            ETG99=EP15_IncentiveItems_0x000000000005CD3C:380220
            GRP99=THESIMS4PC
            TYP99=DEFAULT

            NAM100=The Sims™ 4 Castle Estate Kit
            IID100=SIMS4.OFF.SOLP.0x000000000005F6CC
            ETG100=SP47_CastleEstateKit_0x000000000005F6CC:390860
            GRP100=THESIMS4PC
            TYP100=DEFAULT

            NAM101=The Sims™ 4 Goth Galore Kit
            IID101=SIMS4.OFF.SOLP.0x000000000005F6CE
            ETG101=SP48_GothGaloreKit_0x000000000005F6CE:390862
            GRP101=THESIMS4PC
            TYP101=DEFAULT

            NAM102=The Sims™ 4 Crystal Creations Stuff Pack
            IID102=SIMS4.OFF.SOLP.0x000000000005B63F
            ETG102=SP49_ProjectCobalt_0x000000000005B63F:374335
            GRP102=THESIMS4PC
            TYP102=DEFAULT

            NAM103=The Sims™ 4 Urban Homage Kit
            IID103=SIMS4.OFF.SOLP.0x0000000000063060
            ETG103=SP50_UrbanHomageKit_0x0000000000063060:405600
            GRP103=THESIMS4PC
            TYP103=DEFAULT

            NAM104=The Sims™ 4 Party Essentials Kit
            IID104=SIMS4.OFF.SOLP.0x0000000000063061
            ETG104=SP51_PartyEssentialsKit_0x0000000000063061:405601
            GRP104=THESIMS4PC
            TYP104=DEFAULT

            NAM105=The Sims™ 4 Riviera Retreat Kit
            IID105=SIMS4.OFF.SOLP.0x00000000000661D4
            ETG105=SP52_RivieraRetreatKit_0x00000000000661D4:418260
            GRP105=THESIMS4PC
            TYP105=DEFAULT

            NAM106=The Sims™ 4 Cozy Bistro Kit
            IID106=SIMS4.OFF.SOLP.0x00000000000661D5
            ETG106=SP53_CozyBistroKit_0x00000000000661D5:418261
            GRP106=THESIMS4PC
            TYP106=DEFAULT

            NAM107=The Sims™ 4 Lovestruck (normal)
            IID107=SIMS4.OFF.SOLP.0x0000000000063821
            ETG107=EP16_Lovestruck_0x0000000000063821:407585
            GRP107=THESIMS4PC
            TYP107=DEFAULT

            NAM108=The Sims™ 4 Lovestruck (preorder)
            IID108=SIMS4.OFF.SOLP.0x0000000000064B65
            ETG108=EP16_PrePurchase_0x0000000000064B65:412517
            GRP108=THESIMS4PC
            TYP108=DEFAULT

            NAM109=The Sims™ 4 Lovestruck (bonuses only)
            IID109=SIMS4.OFF.SOLP.0x0000000000064B64
            ETG109=EP16_IncentiveItems_0x0000000000064B64:412516
            GRP109=THESIMS4PC
            TYP109=DEFAULT

            NAM110=The Sims™ 4 Artist Studio Kit
            IID110=SIMS4.OFF.SOLP.0x000000000006A8BA
            ETG110=SP54_ArtistStudioKit_0x000000000006A8BA:436410
            GRP110=THESIMS4PC
            TYP110=DEFAULT

            NAM111=The Sims™ 4 Storybook Nursery Kit
            IID111=SIMS4.OFF.SOLP.0x000000000006A8B1
            ETG111=SP55_StorybookNurseryKit_0x000000000006A8B1:436401
            GRP111=THESIMS4PC
            TYP111=DEFAULT

            NAM112=The Sims™ 4 Life & Death
            IID112=SIMS4.OFF.SOLP.0x0000000000069DFF
            ETG112=EP17_LifeAndDeath_0x0000000000069DFF:433663
            GRP112=THESIMS4PC
            TYP112=DEFAULT

            NAM113=The Sims™ 4 Sweet Slumber Party Kit
            IID113=SIMS4.OFF.SOLP.0x000000000006E7DF
            ETG113=SP56_SweetSlumberPartyKit_0x000000000006E7DF:452575
            GRP113=THESIMS4PC
            TYP113=DEFAULT

            NAM114=The Sims™ 4 Cozy Kitsch Kit
            IID114=SIMS4.OFF.SOLP.0x000000000006E7E0
            ETG114=SP57_CozyKitschKit_0x000000000006E7E0:452576
            GRP114=THESIMS4PC
            TYP114=DEFAULT

            NAM115=The Sims™ 4 Comfy Gamer Kit
            IID115=SIMS4.OFF.SOLP.0x000000000006E828
            ETG115=SP58_CozyGamerClutter_0x000000000006E828:452648
            GRP115=THESIMS4PC
            TYP115=DEFAULT

            NAM116=The Sims™ 4 Secret Sanctuary Kit
            IID116=SIMS4.OFF.SOLP.0x000000000006F575
            ETG116=SP59_SecretSanctuaryKit_0x000000000006F575:456053
            GRP116=THESIMS4PC
            TYP116=DEFAULT

            NAM117=The Sims™ 4 Casanova Cave Kit
            IID117=SIMS4.OFF.SOLP.0x000000000006F576
            ETG117=SP60_CasanovaCaveKit_0x000000000006F576:456054
            GRP117=THESIMS4PC
            TYP117=DEFAULT

            NAM118=The Sims™ 4 Refined Living Room Kit
            IID118=SIMS4.OFF.SOLP.0x0000000000070A7F
            ETG118=SP61_RefinedLivingRoom_0x0000000000070A7F:461439
            GRP118=THESIMS4PC
            TYP118=DEFAULT

            NAM119=The Sims™ 4 Business Chic Kit
            IID119=SIMS4.OFF.SOLP.0x0000000000070A80
            ETG119=SP62_BusinessChic_0x0000000000070A80:461440
            GRP119=THESIMS4PC
            TYP119=DEFAULT

            NAM120=The Sims™ 4 Businesses & Hobbies (normal)
            IID120=SIMS4.OFF.SOLP.0x0000000000064E47
            ETG120=EP18_BusinessesAndHobbies_0x0000000000064E47:413255
            GRP120=THESIMS4PC
            TYP120=DEFAULT

            NAM121=The Sims™ 4 Businesses & Hobbies (preorder)
            IID121=SIMS4.OFF.SOLP.0x000000000006A9D8
            ETG121=EP18_PrePurchase_0x000000000006A9D8:436696
            GRP121=THESIMS4PC
            TYP121=DEFAULT

            NAM122=The Sims™ 4 Businesses & Hobbies (bonuses only)
            IID122=SIMS4.OFF.SOLP.0x000000000006A9D7
            ETG122=EP18_IncentiveItems_0x000000000006A9D7:436695
            GRP122=THESIMS4PC
            TYP122=DEFAULT

            NAM123=The Sims™ 4 Sleek Bathroom Kit
            IID123=SIMS4.OFF.SOLP.0x00000000000718AF
            ETG123=SP63_SleekBathroomKit_0x00000000000718AF:465071
            GRP123=THESIMS4PC
            TYP123=DEFAULT

            NAM124=The Sims™ 4 Sweet Allure Kit
            IID124=SIMS4.OFF.SOLP.0x00000000000718B0
            ETG124=SP64_SweetAllureKit_0x00000000000718B0:465072
            GRP124=THESIMS4PC
            TYP124=DEFAULT

            NAM125=The Sims™ 4 Restoration Workshop Kit
            IID125=SIMS4.OFF.SOLP.0x0000000000073489
            ETG125=SP65_RestorationWorkshopKit_0x0000000000073489:472201
            GRP125=THESIMS4PC
            TYP125=DEFAULT

            NAM126=The Sims™ 4 Kitchen Clutter Kit
            IID126=SIMS4.OFF.SOLP.0x000000000007348B
            ETG126=SP67_KitchenClutterKit_0x000000000007348B:472203
            GRP126=THESIMS4PC
            TYP126=DEFAULT

            NAM127=The Sims™ 4 Golden Years Kit
            IID127=SIMS4.OFF.SOLP.0x000000000007348A
            ETG127=SP66_GoldenYearsKit_0x000000000007348A:472202
            GRP127=THESIMS4PC
            TYP127=DEFAULT

            NAM128=The Sims™ 4 Fairy Garden Digital Content
            IID128=SIMS4.OFF.SOLP.0x0000000000073122
            ETG128=EP19_IncentiveItems_0x0000000000073122:471330
            GRP128=THESIMS4PC
            TYP128=DEFAULT

            NAM129=The Sims™ 4 Enchanted by Nature Expansion Pack
            IID129=SIMS4.OFF.SOLP.0x0000000000073123
            ETG129=EP19_PrePurchase_0x0000000000073123:471331
            GRP129=THESIMS4PC
            TYP129=DEFAULT

            NAM130=The Sims™ 4 Essential Glam Kit
            IID130=SIMS4.OFF.SOLP.0x0000000000076F8B
            ETG130=SP72_EssentialGlam_0x0000000000076F8B:487307
            GRP130=THESIMS4PC
            TYP130=DEFAULT

            NAM131=The Sims™ 4 Grange Mudroom Kit
            IID131=SIMS4.OFF.SOLP.0x0000000000076F8A
            ETG131=SP71_GrangeMudroom_0x0000000000076F8A:487306
            GRP131=THESIMS4PC
            TYP131=DEFAULT

            NAM132=The Sims™ 4  Warm & Cozy Digital Content
            IID132=SIMS4.OFF.SOLP.0x000000000007789A
            ETG132=EP20_IncentiveItems_0x000000000007789A:489626
            GRP132=THESIMS4PC
            TYP132=DEFAULT

            NAM133=The Sims™ 4 Adventure Awaits Expansion Pack
            IID133=SIMS4.OFF.SOLP.0x000000000007789B
            ETG133=EP20_PrePurchase_0x000000000007789B:489627
            GRP133=THESIMS4PC
            TYP133=DEFAULT

            NAM134=The Sims™ 4 Autumn Apparel Kit
            IID134=SIMS4.OFF.SOLP.0x0000000000078094
            ETG134=SP69_AutumnApparel_0x0000000000078094:491668
            GRP134=THESIMS4PC
            TYP134=DEFAULT

            NAM135=The Sims™ 4 Modern Retreat Kit
            IID135=SIMS4.OFF.SOLP.0x000000000007D6AF
            ETG135=SP73_ModernRetreat_0x000000000007D6AF:513711
            GRP135=THESIMS4PC
            TYP135=DEFAULT

            NAM136=The Sims™ 4 Garden to Table Kit
            IID136=SIMS4.OFF.SOLP.0x000000000007D390
            ETG136=SP74_GardentoTable_0x000000000007D390:512912
            GRP136=THESIMS4PC
            TYP136=DEFAULT

            NAM137=The Sims™ 4 SpongeBob’s House Kit
            IID137=SIMS4.OFF.SOLP.0x000000000007AD3F
            ETG137=SP68_SpongeBobsHouse_0x000000000007AD3F:503103
            GRP137=THESIMS4PC
            TYP137=DEFAULT

            NAM138=The Sims™ 4 SpongeBob’s House Kit Bonus Items
            IID138=SIMS4.OFF.SOLP.0x000000000007B590
            ETG138=SP68_BonusItems_0x000000000007B590:505232
            GRP138=THESIMS4PC
            TYP138=DEFAULT

            NAM139=The Sims™ 4 SpongeBob Kid’s Room Kit
            IID139=SIMS4.OFF.SOLP.0x000000000007AD40
            ETG139=SP70_SpongeBobKidsRoom_0x000000000007AD40:503104
            GRP139=THESIMS4PC
            TYP139=DEFAULT

            NAM140=The Sims™ 4 SpongeBob Kid’s Room Kit Bonus Items
            IID140=SIMS4.OFF.SOLP.0x000000000007B5A4
            ETG140=SP70_BonusItems_0x000000000007B5A4:505252
            GRP140=THESIMS4PC
            TYP140=DEFAULT

            NAM141=The Sims™ 4 Prairie Dreams Kit
            IID141=SIMS4.OFF.SOLP.0x000000000007EFF9
            ETG141=SP81_PrairieDreams_0x000000000007EFF9:520185
            GRP141=THESIMS4PC
            TYP141=DEFAULT

            NAM142=The Sims™ 4  Silver Screen Style Kit
            IID142=SIMS4.OFF.SOLP.0x000000000007F617
            ETG142=SP76_SilverScreenStyle_0x000000000007F617:521751
            GRP142=THESIMS4PC
            TYP142=DEFAULT

            NAM143=The Sims™ 4 Tea Time Solarium Kit
            IID143=SIMS4.OFF.SOLP.0x000000000007F618
            ETG143=SP77_TeaTimeSolarium_0x000000000007F618:521752
            GRP143=THESIMS4PC
            TYP143=DEFAULT

            NAM144=The Sims™ 4  Regal Treasures Digital Content
            IID144=SIMS4.OFF.SOLP.0x000000000007B9F0
            ETG144=EP21_IncentiveItems_0x000000000007B9F0:506352
            GRP144=THESIMS4PC
            TYP144=DEFAULT

            NAM145=The Sims™ 4 Royalty & Legacy Expansion Pack
            IID145=SIMS4.OFF.SOLP.0x000000000007B9F1
            ETG145=EP21_PrePurchase_0x000000000007B9F1:506353
            GRP145=THESIMS4PC
            TYP145=DEFAULT
            
            NAM146=The Sims™ 4 Wonderland Playroom Kit
            IID146=SIMS4.OFF.SOLP.0x000000000007F616
            ETG146=SP75_WonderlandPlayroom_0x000000000007F616:521750
            GRP146=THESIMS4PC
            TYP146=DEFAULT

            NAM147=The Sims™ 4 Yard Charm (Creator) Kit
            IID147=SIMS4.OFF.SOLP.0x00000000000810FE
            ETG147=SP82_YardCharm_0x00000000000810FE:528638
            GRP147=THESIMS4PC
            TYP147=DEFAULT
            
            """;

    }
}
