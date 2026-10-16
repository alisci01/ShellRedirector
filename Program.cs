using System.Management;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace ShellRedirector
{
    // The whole program is Windows-only (WMI + WinForms), so declare that up front and let the
    // compiler stop CA1416 from flagging every Windows-only API we call.
    [SupportedOSPlatform("windows")]
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

        // Waits for the process to exit, then for any processes it spawned (for example, Steam's
        // updater), and so on down the tree, since a launcher process usually exits right away.
        internal static void WaitForExitAndChildren(this Process RootProcess)
        {
            List<Process> ProcessesToWaitForExit = new List<Process>() { RootProcess };

            while (ProcessesToWaitForExit.Count > 0)
            {
                var CurrentProcessToWait = ProcessesToWaitForExit[0];
                ProcessesToWaitForExit.RemoveAt(0);

                try
                {
                    CurrentProcessToWait.WaitForExit();
                    ProcessesToWaitForExit.AddRange(CurrentProcessToWait.GetChildProcesses());
                }
                catch (InvalidOperationException)
                {
                    // No process handle to wait on (can happen when UseShellExecute is true and the
                    // shell didn't hand us one), or the process vanished before we got to it.
                }
                finally
                {
                    if (CurrentProcessToWait != RootProcess)
                    {
                        CurrentProcessToWait.Dispose();
                    }
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal class Program
    {
        static void Main(string[] args)
        {
            string ShellRedirectorLocalAppPath = Path.Combine(Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ShellRedirector");
            string ShellRedirectorSettignsPath = Path.Combine(ShellRedirectorLocalAppPath, "settings.json");

            Settings RedirectorSettings = SettingsUpgrader.Load(ShellRedirectorSettignsPath, out bool bUpgradedSettings);

            // Query the Win32_PnPEntity class for all PnP devices
            string query = "SELECT * FROM Win32_PnPEntity";
            SettingsEntry MatchedEntry = null;
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    // Win32_PnPEntity also lists device nodes that aren't currently connected
                    // (phantom/ghost devices), plus disabled or failed devices, and those still have
                    // a DeviceID. Only act on devices that are actually there and healthy.
                    string Status = device["Status"] != null ? device["Status"].ToString() : "";
                    if (Status != "OK")
                    {
                        continue;
                    }

                    string DeviceID = device["DeviceID"] != null ? device["DeviceID"].ToString() : "";
                    if (!string.IsNullOrEmpty(DeviceID))
                    {
                        MatchedEntry = SettingsUpgrader.FindEntryForDevice(RedirectorSettings, DeviceID);
                        if (MatchedEntry != null)
                        {
                            break;
                        }
                    }
                }
            }

            // Nothing else in this run changes the settings, so only write the file back out when
            // it was written by an older version of the program.
            if (bUpgradedSettings)
            {
                SettingsUpgrader.Save(ShellRedirectorLocalAppPath, ShellRedirectorSettignsPath, RedirectorSettings);
            }

            if (MatchedEntry != null)
            {
                RunEntry(MatchedEntry);
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

        // Launches the entry's command, waits for it and all of its child processes to exit,
        // then returns so the caller can start explorer.
        static void RunEntry(SettingsEntry Entry)
        {
            if (string.IsNullOrWhiteSpace(Entry.Command))
            {
                return;
            }

            ProcessStartInfo EntryStartInfo = new ProcessStartInfo
            {
                FileName = Entry.Command, // The application to run
                Arguments = Entry.Arguments, // Optional command-line arguments
                UseShellExecute = true,
                CreateNoWindow = false,
            };

            try
            {
                using (Process EntryProcess = Process.Start(EntryStartInfo))
                {
                    EntryProcess.WaitForExitAndChildren();
                }
            }
            catch (Exception e)
            {
                MessageBox.Show($"Failed to run {Entry.Command}: {e.Message}", "ShellRedirector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
