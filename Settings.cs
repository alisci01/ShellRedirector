using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShellRedirector
{
    // Version numbers for the settings.json format.
    // When the format changes: add a new constant, point Current at it, and add a matching
    // step to SettingsUpgrader.Upgrade() so files written by older versions get migrated.
    internal static class SettingsVersion
    {
        // The old format: no "version" field at all, just SteamMonitors / ExplorerMonitors.
        public const int Legacy = 0;

        // First versioned format: a list of entries, each holding the device IDs that trigger it
        // and the command to run when one of those device IDs is detected.
        public const int CommandEntries = 1;

        // The format this build of ShellRedirector reads and writes.
        public const int Current = CommandEntries;
    }

    // One settings entry: if any device ID in DeviceIDs is detected, Command/Arguments is launched,
    // and ShellRedirector waits for that process (and all of its child processes) to exit before
    // starting explorer.
    internal class SettingsEntry
    {
        public string Name { get; set; } = "";
        public List<string> DeviceIDs { get; set; } = new List<string>();
        public string Command { get; set; } = "";
        public string Arguments { get; set; } = "";

        public bool MatchesDevice(string DeviceID)
        {
            return DeviceIDs.Any(ExistingDeviceID => string.Equals(ExistingDeviceID, DeviceID, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal class Settings
    {
        public int Version { get; set; } = SettingsVersion.Legacy;
        public List<SettingsEntry> Entries { get; set; } = new List<SettingsEntry>();

        // Legacy (SettingsVersion.Legacy) fields, kept only so that old settings files can be read
        // and upgraded. They are emptied out by the upgrade to SettingsVersion.CommandEntries.
        public List<string> SteamMonitors { get; set; } = new List<string>();
        public List<string> ExplorerMonitors { get; set; } = new List<string>();
    }

    internal static class SettingsUpgrader
    {
        internal const string SteamCommand = @"C:\Program Files (x86)\Steam\steam.exe";
        internal const string SteamArguments = "steam://open/bigpicture";
        internal const string SteamEntryName = "Steam Big Picture";

        // Hand-edited settings files are common, so be forgiving when reading them.
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // WriteIndented so the file is readable, and a relaxed encoder so the '&' and '<' in device
        // IDs aren't escaped to \u0026 / \u003C, which makes them impossible to read or copy by hand.
        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // Reads settings.json (if it exists) and upgrades it in memory to the current format.
        // bUpgraded tells the caller the file on disk is out of date and needs saving.
        internal static Settings Load(string SettingsPath, out bool bUpgraded)
        {
            Settings LoadedSettings = new Settings { Version = SettingsVersion.Current };
            bUpgraded = false;

            if (Path.Exists(SettingsPath))
            {
                try
                {
                    using (var SettingsFileStream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read))
                    {
                        var DeserializedSettings = JsonSerializer.Deserialize<Settings>(SettingsFileStream, ReadOptions);
                        if (DeserializedSettings != null)
                        {
                            LoadedSettings = DeserializedSettings;
                        }
                    }
                }
                catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
                {
                    // Unreadable or corrupt settings: fall back to defaults in the current format.
                    LoadedSettings = new Settings { Version = SettingsVersion.Current };
                    bUpgraded = true;
                }
            }

            // Normalize before upgrading so the upgrade steps can assume the lists exist,
            // and again afterwards in case an upgrade step introduced something new.
            Normalize(LoadedSettings);
            bUpgraded |= Upgrade(LoadedSettings);
            Normalize(LoadedSettings);

            return LoadedSettings;
        }

        internal static void Save(string SettingsDirectory, string SettingsPath, Settings SettingsToSave)
        {
            if (!Path.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            using (var SettingsFileStream = new FileStream(SettingsPath, FileMode.Create))
            {
                JsonSerializer.Serialize(SettingsFileStream, SettingsToSave, WriteOptions);
            }
        }

        // Walks an older settings object forward one version at a time until it matches the
        // format this build uses. Returns true if anything changed.
        internal static bool Upgrade(Settings SettingsToUpgrade)
        {
            bool bChanged = false;

            // A settings file may be several versions behind us, so keep stepping through the
            // upgrades until we reach the current version.
            while (SettingsToUpgrade.Version < SettingsVersion.Current)
            {
                int VersionBeforeUpgrade = SettingsToUpgrade.Version;

                switch (VersionBeforeUpgrade)
                {
                    case SettingsVersion.Legacy:
                        UpgradeLegacyToCommandEntries(SettingsToUpgrade);
                        break;

                    default:
                        // A version we don't know how to upgrade from; leave the file alone.
                        return bChanged;
                }

                if (SettingsToUpgrade.Version <= VersionBeforeUpgrade)
                {
                    // The upgrade step didn't advance the version - stop instead of looping forever.
                    return bChanged;
                }

                bChanged = true;
            }

            return bChanged;
        }

        // SettingsVersion.Legacy -> SettingsVersion.CommandEntries
        // Every monitor ID that used to live in SteamMonitors becomes a device ID on a single
        // entry that launches Steam in Big Picture mode, exactly like the old hardcoded code did.
        // ExplorerMonitors was only ever used to silence the "unrecognized monitor" prompt, which
        // no longer exists, so its IDs are dropped.
        internal static void UpgradeLegacyToCommandEntries(Settings SettingsToUpgrade)
        {
            if (SettingsToUpgrade.SteamMonitors != null && SettingsToUpgrade.SteamMonitors.Count > 0)
            {
                SettingsEntry SteamEntry = GetSteamEntry(SettingsToUpgrade);

                foreach (string DeviceID in SettingsToUpgrade.SteamMonitors)
                {
                    if (!string.IsNullOrEmpty(DeviceID) && !SteamEntry.MatchesDevice(DeviceID))
                    {
                        SteamEntry.DeviceIDs.Add(DeviceID);
                    }
                }

                // The entry list replaces SteamMonitors, so clear out the legacy field.
                SettingsToUpgrade.SteamMonitors.Clear();
            }

            SettingsToUpgrade.ExplorerMonitors?.Clear();

            SettingsToUpgrade.Version = SettingsVersion.CommandEntries;
        }

        // Returns the entry that would run for this device ID, or null if none of them match.
        internal static SettingsEntry FindEntryForDevice(Settings SettingsToSearch, string DeviceID)
        {
            foreach (SettingsEntry Entry in SettingsToSearch.Entries)
            {
                if (Entry.MatchesDevice(DeviceID))
                {
                    return Entry;
                }
            }

            return null;
        }

        // Returns the Steam Big Picture entry, creating it if the settings don't have one yet.
        // Used to hold the device IDs migrated from the legacy SteamMonitors list.
        internal static SettingsEntry GetSteamEntry(Settings SettingsToSearch)
        {
            SettingsEntry SteamEntry = SettingsToSearch.Entries.FirstOrDefault(Entry =>
                string.Equals(Entry.Command, SteamCommand, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Entry.Arguments, SteamArguments, StringComparison.OrdinalIgnoreCase));

            if (SteamEntry == null)
            {
                SteamEntry = new SettingsEntry
                {
                    Name = SteamEntryName,
                    Command = SteamCommand,
                    Arguments = SteamArguments,
                };

                SettingsToSearch.Entries.Add(SteamEntry);
            }

            return SteamEntry;
        }

        // Fills in lists/strings that were explicitly null in the file so the rest of the
        // program can use them without null checks.
        private static void Normalize(Settings SettingsToNormalize)
        {
            SettingsToNormalize.Entries ??= new List<SettingsEntry>();
            SettingsToNormalize.Entries.RemoveAll(Entry => Entry == null);
            SettingsToNormalize.ExplorerMonitors ??= new List<string>();
            SettingsToNormalize.SteamMonitors ??= new List<string>();

            foreach (SettingsEntry Entry in SettingsToNormalize.Entries)
            {
                Entry.DeviceIDs ??= new List<string>();
                Entry.Name ??= "";
                Entry.Command ??= "";
                Entry.Arguments ??= "";
            }
        }
    }
}
