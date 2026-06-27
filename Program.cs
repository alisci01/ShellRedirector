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
                    SteamProcess.WaitForExit(); // Blocks here until Notepad is closed
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

