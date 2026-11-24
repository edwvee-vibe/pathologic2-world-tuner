using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// Fog presets: every config entry of the sections "Fog" and "Fog.*" (mode, volumetric light, camera fog shader, the global
    /// profile and every weather's profile). Built-in presets are defaults plus a few overrides; user presets are text files
    /// in BepInEx\config\pathologic2-world-tuner-fog-presets\ (one "Section.Key = value" line per setting), so they can be
    /// copied and shared. Files are read when the Fog tab is shown and after saving or deleting, never per frame.
    /// </summary>
    internal static class FogPresets
    {
        internal sealed class Preset
        {
            public string Name;
            public bool BuiltIn;
            public string File;
            public Dictionary<string, string> Values; // "Section.Key" -> serialized value; built-ins: overrides of the defaults
        }

        private static readonly string Folder = Path.Combine(Paths.ConfigPath, "pathologic2-world-tuner-fog-presets");
        internal static readonly List<Preset> All = new List<Preset>();
        private static bool loaded;

        private static readonly Preset[] BuiltIns =
        {
            BuiltIn("Game"),
            BuiltIn("Lighter", "Fog.Mode", "Global", "Fog.Global.DensityScale", "0.6", "Fog.Global.StartOffset", "10"),
            BuiltIn("Clear view", "Fog.Mode", "Global", "Fog.Global.DensityScale", "0.35", "Fog.Global.StartOffset", "40",
                "Fog.Global.FogginessScale", "0.6"),
            BuiltIn("No fog", "Fog.Mode", "Global", "Fog.Global.DensityScale", "0", "Fog.Global.FogginessScale", "0.3"),
            BuiltIn("Thick", "Fog.Mode", "Global", "Fog.Global.DensityScale", "2", "Fog.Global.FogginessScale", "1.5")
        };

        private static Preset BuiltIn(string name, params string[] pairs)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                values[pairs[i]] = pairs[i + 1];
            }
            return new Preset { Name = name, BuiltIn = true, Values = values };
        }

        private static string Id(ConfigDefinition d) => d.Section + "." + d.Key;

        private static List<ConfigEntryBase> entries;
        private static int entriesCount = -1;

        /// <summary>All fog settings of the config, in a stable order (rebuilt when settings are added, e.g. a new weather).</summary>
        private static List<ConfigEntryBase> FogEntries()
        {
            if (entries != null && entriesCount == Plugin.Cfg.Count)
            {
                return entries;
            }
            entriesCount = Plugin.Cfg.Count;
            var list = new List<ConfigEntryBase>();
            foreach (ConfigDefinition d in Plugin.Cfg.Keys)
            {
                if (d.Section.Equals("Fog", StringComparison.OrdinalIgnoreCase) ||
                    d.Section.StartsWith("Fog.", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(Plugin.Cfg[d]);
                }
            }
            entries = list.OrderBy(e => e.Definition.Section).ThenBy(e => e.Definition.Key).ToList();
            return entries;
        }

        internal static void Reload()
        {
            All.Clear();
            All.AddRange(BuiltIns);
            try
            {
                if (Directory.Exists(Folder))
                {
                    foreach (string file in Directory.GetFiles(Folder, "*.cfg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        Preset p = Read(file);
                        if (p != null)
                        {
                            All.Add(p);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("fogpresets-read", "Fog presets: " + e.Message);
            }
            loaded = true;
        }

        internal static void EnsureLoaded()
        {
            if (!loaded)
            {
                Reload();
            }
        }

        private static Preset Read(string file)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string name = Path.GetFileNameWithoutExtension(file);
            foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    if (line.StartsWith("# Name:", StringComparison.Ordinal))
                    {
                        name = line.Substring(7).Trim();
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            return values.Count == 0 ? null : new Preset { Name = name, Values = values, File = file };
        }

        private static float nextMatch;
        private static string matching;

        /// <summary>Name of the preset the current settings equal, re-checked twice a second while the tab is shown.</summary>
        internal static string Matching(float now, bool force)
        {
            if (force || now >= nextMatch)
            {
                nextMatch = now + 0.5f;
                matching = All.FirstOrDefault(Matches)?.Name;
            }
            return matching;
        }

        internal static Preset Find(string name)
        {
            return All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The value the preset gives a setting: its own, or for built-ins the default.</summary>
        private static string ValueOf(Preset p, ConfigEntryBase e)
        {
            if (p.Values.TryGetValue(Id(e.Definition), out string v))
            {
                return v;
            }
            return p.BuiltIn ? TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType) : null;
        }

        internal static void Apply(Preset p)
        {
            FogTweaks.BeginBatch();
            try
            {
                foreach (ConfigEntryBase e in FogEntries())
                {
                    string v = ValueOf(p, e);
                    if (v != null && e.GetSerializedValue() != v)
                    {
                        try
                        {
                            e.SetSerializedValue(v);
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogOnce("fogpreset-" + Id(e.Definition), $"Fog preset {p.Name}: {Id(e.Definition)} = {v}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                FogTweaks.EndBatch();
            }
            Plugin.RequestSave();
            Plugin.ShowMessage("Fog preset: " + p.Name);
        }

        /// <summary>Whether the current settings are exactly the preset's (cheap: a few dozen string compares).</summary>
        internal static bool Matches(Preset p)
        {
            foreach (ConfigEntryBase e in FogEntries())
            {
                string v = ValueOf(p, e);
                if (v != null && !SameValue(e.GetSerializedValue(), v))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameValue(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                   float.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                   Math.Abs(x - y) < 0.0001f;
        }

        internal static bool IsValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 40 &&
                   name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                   !BuiltIns.Any(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static void Save(string name)
        {
            name = name.Trim();
            try
            {
                Directory.CreateDirectory(Folder);
                var sb = new StringBuilder();
                sb.AppendLine("# World Tuner fog preset");
                sb.AppendLine("# Name: " + name);
                foreach (ConfigEntryBase e in FogEntries())
                {
                    sb.Append(Id(e.Definition)).Append(" = ").AppendLine(e.GetSerializedValue());
                }
                File.WriteAllText(Path.Combine(Folder, name + ".cfg"), sb.ToString(), new UTF8Encoding(false));
                Plugin.ShowMessage("Fog preset saved: " + name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Fog preset save failed: " + e.Message);
                Plugin.ShowMessage("Fog preset: saving failed (see the log)");
            }
            Reload();
        }

        internal static void Delete(Preset p)
        {
            if (p == null || p.BuiltIn)
            {
                return;
            }
            try
            {
                string file = p.File ?? Path.Combine(Folder, p.Name + ".cfg");
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
                Plugin.ShowMessage("Fog preset deleted: " + p.Name);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Fog preset delete failed: " + e.Message);
            }
            Reload();
        }
    }
}
