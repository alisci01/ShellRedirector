using System.Management;
using System.Windows.Forms;
using System.Diagnostics;

namespace ShellRedirector
{
    internal class Settings
    {
        public List<string> SteamMonitors { get; set; } = new List<string>();
        public List<string> ExplorerMonitors { get; set; } = new List<string>();
    }

    internal static class ProcessExtensions
    {
        internal static List<Process> GetChildProcesses(this Process ParentProcess)
        {
            var Children = new List<Process>();

            // Query WMI for processes where the Parent is our process ID
            string Query = $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {ParentProcess.Id}";

            using (var Searcher = new ManagementObjectSearcher(Query))
            using (var Results = Searcher.Get())
            {
                foreach (ManagementObject MO in Results)
                {
                    int ChildPid = Convert.ToInt32(MO["ProcessId"]);
                    try
                    {
                        // Convert the PID back into a trackable .NET Process object
                        Children.Add(Process.GetProcessById(ChildPid));
                    }
                    catch (ArgumentException)
                    {
                        // The process might have exited between the query and this line
                    }
                }
            }
            return Children;
        }
    }

    internal class Program
    {
        static void Main(string[] args)
        {
            //using System.Management;

            // Query the Win32_PnPEntity class for all PnP devices
            string query = "SELECT * FROM Win32_PnPEntity";
            string ShellRedirectorLocalAppPath = Path.Combine(Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ShellRedirector");
            string ShellRedirectorSettignsPath = Path.Combine(ShellRedirectorLocalAppPath, "settings.json");

            Settings RedirectorSettings = new Settings();
            if (System.IO.Path.Exists(ShellRedirectorSettignsPath))
            {
                using (var SettingsFileStream = new FileStream(ShellRedirectorSettignsPath, FileMode.Open))
                {
                    var DeserializedSettings = System.Text.Json.JsonSerializer.Deserialize<Settings>(SettingsFileStream);
                    if (DeserializedSettings != null)
                    {
                        RedirectorSettings = DeserializedSettings;
                    }
                }
            }

            bool bStartSteam = false;
            bool bDirtySettings = false;
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    string PNPClass = device["PNPClass"] != null ? device["PNPClass"].ToString() : "";
                    string Status = device["Status"] != null ? device["Status"].ToString() : "";

                    if (PNPClass == "Monitor" && Status == "OK")
                    {
                        string DeviceID = device["DeviceID"] != null ? device["DeviceID"].ToString() : "";
                        if (!string.IsNullOrEmpty(DeviceID))
                        {
                            bStartSteam = RedirectorSettings.SteamMonitors.Contains(DeviceID);
                            bool bIgnore = RedirectorSettings.ExplorerMonitors.Contains(DeviceID);

                            if (!bStartSteam && !bIgnore)
                            {
                                DialogResult Result = MessageBox.Show($"Unrecognized monitor {DeviceID}. Do you want to mark it as a monitor for Steam Big Picture?", "Unrecognized Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                                if (Result == DialogResult.Yes)
                                {
                                    RedirectorSettings.SteamMonitors.Add(DeviceID);
                                    bStartSteam = true;
                                }
                                else
                                {
                                    RedirectorSettings.ExplorerMonitors.Add(DeviceID);
                                }

                                bDirtySettings = true;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (bDirtySettings)
            {
                if (!Path.Exists(ShellRedirectorLocalAppPath))
                {
                    System.IO.Directory.CreateDirectory(ShellRedirectorLocalAppPath);
                }

                using (var SettingsFileStream = new FileStream(ShellRedirectorSettignsPath, FileMode.Create))
                {
                    System.Text.Json.JsonSerializer.Serialize<Settings>(SettingsFileStream, RedirectorSettings);
                }
            }

            if (bStartSteam)
            {
                ProcessStartInfo SteamStartInfo = new ProcessStartInfo
                {
                    FileName = "C:\\Program Files (x86)\\Steam\\steam.exe", // The application to run
                    Arguments = "steam://open/bigpicture", // Optional command-line arguments
                };

                using (Process SteamProcess = Process.Start(SteamStartInfo))
                {
                    List<Process> ProcessesToWaitForExit = new List<Process>() { SteamProcess };

                    // the original process may have spawned child processes (for example, Steam's update)
                    //  so we should wait for the child processes to exit too
                    while (ProcessesToWaitForExit.Count > 0)
                    {
                        var CurrentProcessToWait = ProcessesToWaitForExit[0];
                        CurrentProcessToWait.WaitForExit();

                        ProcessesToWaitForExit.RemoveAt(0);
                        ProcessesToWaitForExit.AddRange(CurrentProcessToWait.GetChildProcesses());
                    }
                }
            }

            ProcessStartInfo ExplorerStartInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            Process.Start(ExplorerStartInfo);
            Environment.Exit(0);
        }
    }
}
