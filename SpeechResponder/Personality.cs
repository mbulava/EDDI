﻿using EddiEvents;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Utilities;

namespace EddiSpeechResponder
{
    /// <summary>
    /// A personality is a combination of scripts used to respond to specific events
    /// </summary>
    public class Personality
    {
        [JsonProperty("name")]
        public string Name { get; private set; }

        [JsonProperty("description")]
        public string Description { get; private set; }

        [JsonIgnore]
        public bool IsDefault { get; set; } = false;

        [JsonIgnore]
        public bool IsCustom => !IsDefault;

        [JsonProperty("scripts")]
        public Dictionary<string, Script> Scripts { get; private set; }

        [JsonIgnore]
        private string dataPath;

        [JsonIgnore]
        static readonly object fileLock = new object();

        private static readonly string DEFAULT_PATH = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)).FullName + @"\" + Properties.SpeechResponder.default_personality_script_filename;
        private static readonly string DEFAULT_USER_PATH = Constants.DATA_DIR + @"\personalities\" + Properties.SpeechResponder.default_personality_script_filename;

        public Personality(string name, string description, Dictionary<string, Script> scripts)
        {
            // Ensure that the name doesn't have any illegal characters
            string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            Regex r = new Regex(string.Format("[{0}]", Regex.Escape(regexSearch)));
            Name = r.Replace(name, "");

            Name = name;
            Description = description;
            Scripts = scripts;
        }

        [JsonIgnore]
        public bool IsEditable
        {
            get { return (Name != "EDDI"); }
            set { }
        }

        /// <summary>
        /// Obtain all personalities from a directory.  If the directory name is not supplied the
        /// default of Constants.Data_DIR\personalities is used
        /// </summary>
        public static List<Personality> AllFromDirectory(string directory = null)
        {
            List<Personality> personalities = new List<Personality>();
            if (directory == null)
            {
                directory = Constants.DATA_DIR + @"\personalities";
                Directory.CreateDirectory(directory);
            }
            foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("*.json", SearchOption.AllDirectories))
            {
                Personality personality = FromFile(file.FullName);
                if (personality != null)
                {
                    personalities.Add(personality);
                }
            }

            return personalities;
        }

        public static Personality FromName(string name)
        {
            if (name == "EDDI")
            {
                return Default();
            }
            else
            {
                return FromFile(Constants.DATA_DIR + @"\personalities\" + name.ToLowerInvariant() + ".json");
            }
        }

        /// <summary>
        /// Obtain the default personality
        /// </summary>
        public static Personality Default()
        {
            return FromFile(DEFAULT_PATH, true);
        }

        /// <summary>
        /// Obtain personality from a file.
        /// </summary>
        public static Personality FromFile(string filename = null, bool isDefault = false)
        {
            if (filename == null)
            {
                filename = DEFAULT_USER_PATH;
                isDefault = true;
            }

            Personality personality = null;
            string data = Files.Read(filename);
            if (data != null)
            {
                try
                {
                    personality = JsonConvert.DeserializeObject<Personality>(data);
                }
                catch (Exception e)
                {
                    // malformed JSON for some reason: rename so that the user can examine and fix it.
                    string newFileName = filename + ".malformed";
                    if (File.Exists(newFileName))
                    {
                        // no point keeping a history: only the latest is likely to be useful. Pro users will be using version control anyway.
                        File.Delete(newFileName);
                    }
                    File.Move(filename, newFileName);

                    Logging.Error($"Could not parse \"{filename}\": moved to \"{newFileName}\". Error was \"{e.Message}\"");
                }
            }

            if (personality != null)
            {
                personality.dataPath = filename;
                personality.IsDefault = isDefault;
                fixPersonalityInfo(personality);
            }

            return personality;
        }

        /// <summary>
        /// Write personality to a file.
        /// </summary>
        public void ToFile(string filename = null)
        {
            if (filename == null)
            {
                filename = dataPath;
            }
            if (filename == null)
            {
                filename = DEFAULT_USER_PATH;
            }

            if (filename != DEFAULT_PATH)
            {
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                lock (fileLock)
                {
                    Files.Write(filename, json);
                }
            }
        }

        /// <summary>
        /// Create a copy of this file, altering the datapath appropriately
        /// </summary>
        public Personality Copy(string name, string description)
        {
            // Tidy the name up to avoid bad characters
            string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            Regex r = new Regex(string.Format("[{0}]", Regex.Escape(regexSearch)));
            name = r.Replace(name, "");

            // Save a copy of this personality
            string iname = name.ToLowerInvariant();
            string copyPath = Constants.DATA_DIR + @"\personalities\" + iname + ".json";
            // Ensure it doesn't exist
            if (!File.Exists(copyPath))
            {
                ToFile(copyPath);
            }
            // Load the personality back in
            Personality newPersonality = FromFile(copyPath);
            // Change its name and description and save it back out again
            newPersonality.Name = name;
            newPersonality.Description = description;
            newPersonality.ToFile();
            // And finally return it
            return newPersonality;
        }

        public void RemoveFile()
        {
            File.Delete(dataPath);
        }

        /// <summary>
        /// Fix up the personality information to ensure that it contains the correct event
        /// </summary>
        private static void fixPersonalityInfo(Personality personality)
        {
            // Default personality for reference scripts
            Personality defaultPersonality = personality.IsDefault ? null : Default();

            Dictionary<string, Script> fixedScripts = new Dictionary<string, Script>();
            // Ensure that every required event is present
            List<string> missingScripts = new List<string>();
            foreach (KeyValuePair<string, string> defaultEvent in Events.DESCRIPTIONS)
            {
                personality.Scripts.TryGetValue(defaultEvent.Key, out Script script);
                Script defaultScript = null;
                defaultPersonality?.Scripts?.TryGetValue(defaultEvent.Key, out defaultScript);
                script = UpgradeScript(script, defaultScript);
                if (script == null)
                {
                    missingScripts.Add(defaultEvent.Key);
                }
                else
                {
                    fixedScripts.Add(defaultEvent.Key, script);
                }
            }
            foreach (KeyValuePair<string, Script> kv in personality.Scripts)
            {
                if (!fixedScripts.ContainsKey(kv.Key))
                {
                    Script defaultScript = null;
                    defaultPersonality?.Scripts?.TryGetValue(kv.Key, out defaultScript);
                    Script script = UpgradeScript(kv.Value, defaultScript);
                    fixedScripts.Add(kv.Key, script);
                }
            }
            if (!personality.IsDefault)
            {
                // Remove deprecated scripts from the list
                List<string> scriptHolder = new List<string>();
                foreach (KeyValuePair<string, Script> kv in fixedScripts)
                {
                    if (kv.Key == "Jumping") // Replaced by "FSD engaged" script
                    {
                        scriptHolder.Add(kv.Key);
                    }
                    else if (kv.Value.Name == "Crew member role change") // This name is mismatched to the key (should be "changed"), 
                                                                         // so EDDI couldn't match the script name to the .json key correctly. The default script has been corrected.
                    {
                        scriptHolder.Add(kv.Key);
                    }
                    else if (kv.Value.Name == "Ship low fuel") // Accidental duplicate. The real event is called 'Low fuel'
                    {
                        scriptHolder.Add(kv.Key);
                    }
                    else if (kv.Value.Name == "Modification applied") // Event deprecated by FDev, no longer written. 
                    {
                        scriptHolder.Add(kv.Key);
                    }
                    else if (kv.Value.Name == "List launchbays") // Replaced by "Launchbay report" script
                    {
                        scriptHolder.Add(kv.Key);
                    }
                }
                foreach (string script in scriptHolder)
                {
                    fixedScripts.Remove(script);
                }
                // Also add any secondary scripts in the default personality that aren't present in the list
                if (defaultPersonality?.Scripts != null)
                {
                    foreach (KeyValuePair<string, Script> kv in defaultPersonality?.Scripts)
                    {
                        if (!fixedScripts.ContainsKey(kv.Key))
                        {
                            fixedScripts.Add(kv.Key, kv.Value);
                        }
                    }
                }
            }

            // Report missing scripts, except those we have specifically named
            string[] ignoredEventKeys = {

                // Deprecated events
                "Belt scanned",
                "Jumping",
                "Modification applied",
                "Status",

                // Shares updates with monitors / responders but are not intended to be user facing
                "Cargo",
                "Market",
                "Missions",
                "Outfitting",
                "Shipyard",
                "Squadron startup",
                "Stored ships",
                "Stored modules",
                "Unhandled event"
            };

            Array.Sort(ignoredEventKeys);
            bool isIgnoredEvent(string t) => Array.BinarySearch(ignoredEventKeys, t) >= 0;
            missingScripts.RemoveAll(isIgnoredEvent);
            if (missingScripts.Count > 0)
            {
                Logging.Info("Failed to find scripts" + string.Join(";", missingScripts));
            }

            // Re-order the scripts by name
            fixedScripts = fixedScripts.OrderBy(s => s.Key).ToDictionary(s => s.Key, s => s.Value);

            personality.Scripts = fixedScripts;
            personality.ToFile();
        }

        public static Script UpgradeScript(Script personalityScript, Script defaultScript)
        {
            Script script = personalityScript ?? defaultScript;
            if (script != null)
            {
                if (defaultScript != null)
                {
                    if (script.Default)
                    {
                        // This is a default script so take the latest value
                        script.Value = defaultScript.Value;
                    }

                    // Set the default value of our script
                    script.defaultValue = defaultScript.Value;

                    if (defaultScript.Responder)
                    {
                        // This is a responder script so update applicable parameters
                        script.Description = defaultScript.Description;
                        script.Responder = defaultScript.Responder;
                    }
                }
            }

            return script;
        }
    }
}
