using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// A set of presets for one tab: the config entries it covers, built-in presets (the defaults plus a few overrides) and a
    /// folder with the user's own presets, BepInEx\config\pathologic2-world-tuner-&lt;id&gt;-presets\, one text file per preset
    /// ("Section.Key = value" lines), so they can be copied and shared. Files are read when the tab is shown and after saving
    /// or deleting; whether the settings equal a preset is checked twice a second while the tab is shown, never per frame.
    /// </summary>
    internal sealed class PresetStore
    {
        internal sealed class Preset
        {
            public string Name;
            public bool BuiltIn;
            public string File;
            public Dictionary<string, string> Values; // "Section.Key" -> serialized value; built-ins: overrides of the defaults
        }

        public readonly string Id, Title;
        public readonly List<Preset> All = new List<Preset>();
        public int Version; // changes whenever the list is reloaded
        public readonly string Folder;

        private readonly Func<ConfigDefinition, bool> includes;
        private readonly Preset[] builtIns;
        private readonly Action beginApply, endApply;
        private bool loaded;
        private List<ConfigEntryBase> entries;
        private int entriesCount = -1;
        private float nextMatch;
        private string matching;

        // State of the tab's preset row.
        public string NameField = "My preset";
        public string DeleteArmed;
        public string[] ShownNames;
        public Action<int> PickAction;
        public int ShownNamesVersion = -1;

        public PresetStore(string id, string title, Func<ConfigDefinition, bool> includes, Preset[] builtIns,
            Action beginApply = null, Action endApply = null)
        {
            Id = id;
            Title = title;
            this.includes = includes;
            this.builtIns = builtIns;
            this.beginApply = beginApply;
            this.endApply = endApply;
            Folder = Path.Combine(Paths.ConfigPath, $"pathologic2-world-tuner-{id}-presets");
            NameField = "My " + id;
        }

        public static Preset BuiltIn(string name, params string[] pairs)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                values[pairs[i]] = pairs[i + 1];
            }
            return new Preset { Name = name, BuiltIn = true, Values = values };
        }

        public static bool InSections(ConfigDefinition d, params string[] sections)
        {
            foreach (string s in sections)
            {
                if (s.EndsWith(".*") ? d.Section.StartsWith(s.Substring(0, s.Length - 1), StringComparison.OrdinalIgnoreCase) ||
                                       d.Section.Equals(s.Substring(0, s.Length - 2), StringComparison.OrdinalIgnoreCase)
                                     : d.Section.Equals(s, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Key(ConfigDefinition d) => d.Section + "." + d.Key;

        /// <summary>The settings of this set, in a stable order (rebuilt when settings are added, e.g. a new weather).</summary>
        private List<ConfigEntryBase> Entries()
        {
            if (entries != null && entriesCount == Plugin.Cfg.Count)
            {
                return entries;
            }
            entriesCount = Plugin.Cfg.Count;
            entries = Plugin.Cfg.Keys.Where(includes).Select(d => Plugin.Cfg[d])
                .OrderBy(e => e.Definition.Section).ThenBy(e => e.Definition.Key).ToList();
            return entries;
        }

        public void Reload()
        {
            All.Clear();
            All.AddRange(builtIns);
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
                Plugin.LogOnce("presets-read-" + Id, $"{Title} presets: {e.Message}");
            }
            loaded = true;
            Version++;
        }

        public void EnsureLoaded()
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
            foreach (string raw in System.IO.File.ReadAllLines(file, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.StartsWith("# Name:", StringComparison.Ordinal))
                {
                    name = line.Substring(7).Trim();
                    continue;
                }
                if (line.Length == 0 || line.StartsWith("#"))
                {
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

        public Preset Find(string name)
        {
            return All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The value the preset gives a setting: its own, or for built-ins the default.</summary>
        private static string ValueOf(Preset p, ConfigEntryBase e)
        {
            if (p.Values.TryGetValue(Key(e.Definition), out string v))
            {
                return v;
            }
            return p.BuiltIn ? TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType) : null;
        }

        public void Apply(Preset p)
        {
            beginApply?.Invoke();
            try
            {
                foreach (ConfigEntryBase e in Entries())
                {
                    string v = ValueOf(p, e);
                    if (v != null && !SameValue(e.GetSerializedValue(), v))
                    {
                        try
                        {
                            e.SetSerializedValue(v);
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogOnce($"preset-{Id}-{Key(e.Definition)}", $"{Title} preset {p.Name}: {Key(e.Definition)} = {v}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                endApply?.Invoke();
            }
            Plugin.RequestSave();
            Plugin.ShowMessage($"{Title} preset: {p.Name}");
            nextMatch = 0f;
        }

        private bool Matches(Preset p)
        {
            foreach (ConfigEntryBase e in Entries())
            {
                string v = ValueOf(p, e);
                if (v != null && !SameValue(e.GetSerializedValue(), v))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Name of the preset the current settings equal (null = none), re-checked twice a second.</summary>
        public string Matching(float now)
        {
            if (now >= nextMatch)
            {
                nextMatch = now + 0.5f;
                matching = All.FirstOrDefault(Matches)?.Name;
            }
            return matching;
        }

        private static bool SameValue(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                   float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                   Math.Abs(x - y) < 0.0001f;
        }

        public bool IsValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 40 &&
                   name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                   !builtIns.Any(b => string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public void Save(string name)
        {
            name = name.Trim();
            try
            {
                Directory.CreateDirectory(Folder);
                var sb = new StringBuilder();
                sb.AppendLine($"# World Tuner {Title.ToLowerInvariant()} preset");
                sb.AppendLine("# Name: " + name);
                foreach (ConfigEntryBase e in Entries())
                {
                    sb.Append(Key(e.Definition)).Append(" = ").AppendLine(e.GetSerializedValue());
                }
                Preset existing = Find(name);
                string file = existing != null && !existing.BuiltIn && existing.File != null ? existing.File : Path.Combine(Folder, name + ".cfg");
                System.IO.File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                Plugin.ShowMessage($"{Title} preset saved: {name}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Title} preset save failed: {e.Message}");
                Plugin.ShowMessage($"{Title} preset: saving failed (see the log)");
            }
            Reload();
            nextMatch = 0f;
        }

        public void Delete(Preset p)
        {
            if (p == null || p.BuiltIn)
            {
                return;
            }
            try
            {
                string file = p.File ?? Path.Combine(Folder, p.Name + ".cfg");
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                }
                Plugin.ShowMessage($"{Title} preset deleted: {p.Name}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Title} preset delete failed: {e.Message}");
            }
            Reload();
            nextMatch = 0f;
        }
    }

    /// <summary>The preset sets of the window's tabs.</summary>
    internal static class Presets
    {
        private static PresetStore.Preset B(string name, params string[] pairs) => PresetStore.BuiltIn(name, pairs);

        internal static readonly PresetStore Fog = new PresetStore("fog", "Fog",
            d => PresetStore.InSections(d, "Fog.*"),
            new[]
            {
                B("Game"),
                B("Lighter", "Fog.Mode", "Global", "Fog.Global.DensityScale", "0.6", "Fog.Global.StartOffset", "10"),
                B("Clear view", "Fog.Mode", "Global", "Fog.Global.DensityScale", "0.35", "Fog.Global.StartOffset", "40",
                    "Fog.Global.FogginessScale", "0.6"),
                B("No fog", "Fog.Mode", "Global", "Fog.Global.DensityScale", "0", "Fog.Global.FogginessScale", "0.3"),
                B("Thick", "Fog.Mode", "Global", "Fog.Global.DensityScale", "2", "Fog.Global.FogginessScale", "1.5")
            },
            FogTweaks.BeginBatch, FogTweaks.EndBatch);

        internal static readonly PresetStore Distance = new PresetStore("distance", "Distance",
            d => PresetStore.InSections(d, "Distance.*"),
            new[]
            {
                B("Game"),
                B("Far", "Distance.Rendering.Objects", "600", "Distance.Streaming.Houses", "2.5", "Distance.Streaming.Landmarks", "2",
                    "Distance.Streaming.Districts", "1.5", "Distance.Detail.LodBias", "3", "Distance.Detail.ShadowDistance", "120",
                    "Distance.Detail.GrassScale", "2", "Distance.Detail.GroundTexture", "300"),
                B("Very far", "Distance.Rendering.Objects", "1500", "Distance.Streaming.Houses", "4", "Distance.Streaming.Landmarks", "3",
                    "Distance.Streaming.Districts", "2", "Distance.Detail.LodBias", "5", "Distance.Detail.ShadowDistance", "200",
                    "Distance.Detail.GrassScale", "3", "Distance.Detail.GroundTexture", "1000", "Distance.Detail.TerrainError", "8")
            },
            null, DistanceTweaks.MarkDirty);

        // Sky: the Sky tab (rain and wind of Sky.Elements; the falling leaves are on the Life & Lights tab).
        internal static readonly PresetStore Sky = new PresetStore("sky", "Sky",
            d => PresetStore.InSections(d, "Sky.Clouds", "Sky.Day", "Sky.Night", "Sky.Bodies", "Sky.Colors", "Sky.Place") ||
                 (PresetStore.InSections(d, "Sky.Elements") && !d.Key.StartsWith("Leaves", StringComparison.OrdinalIgnoreCase)),
            new[]
            {
                B("Game"),
                B("Lighter nights", "Sky.Night.LightIntensity", "6", "Sky.Night.AmbientMultiplier", "2.5"),
                B("Clear sky", "Sky.Clouds.Coverage", "0.3", "Sky.Elements.Rain", "0"),
                B("Overcast", "Sky.Clouds.Coverage", "3", "Sky.Day.LightIntensity", "0.7"),
                B("Warm light", "Sky.Colors.LightWarmth", "0.4", "Sky.Colors.SkyWarmth", "0.2", "Sky.Colors.AmbientWarmth", "0.3"),
                B("Noon at 12:00", "Sky.Place.UtcOffset", "5.75"),
                B("White nights", "Sky.Place.Latitude", "64", "Sky.Place.DateShiftDays", "-55"),
                B("Polar day", "Sky.Place.Latitude", "75", "Sky.Place.DateShiftDays", "-55"),
                B("Polar night", "Sky.Place.Latitude", "75", "Sky.Place.DateShiftDays", "128"),
                B("Equator", "Sky.Place.Latitude", "0")
            });

        internal static readonly PresetStore Life = new PresetStore("life", "Life & Lights",
            d => PresetStore.InSections(d, "Life", "Lantern") ||
                 (PresetStore.InSections(d, "Sky.Elements") && d.Key.StartsWith("Leaves", StringComparison.OrdinalIgnoreCase)),
            new[]
            {
                B("Game"),
                B("Crowds farther", "Life.CrowdSpawnMinDistance", "120", "Life.CrowdSpawnMaxDistance", "160", "Life.CrowdDestroyDistance", "220"),
                B("Lit streets", "Life.LampShadowDistance", "2", "Life.LampFadeDistance", "2"),
                B("Brighter lantern", "Lantern.Range", "1.5", "Lantern.Brightness", "1.5"),
                B("Lively", "Life.Birds", "2", "Sky.Elements.LeavesRate", "2")
            });

        internal static readonly PresetStore Memory = new PresetStore("memory", "Memory & Loading",
            d => PresetStore.InSections(d, "Memory", "Loading"),
            new[]
            {
                B("Game"),
                B("Faster loading", "Loading.BackgroundPriority", "Normal", "Loading.UploadTimeSliceMs", "4"),
                B("Fewer cleanups", "Memory.CleanupPeriod", "3000")
            });

        internal static readonly PresetStore Image = new PresetStore("image", "Image",
            d => PresetStore.InSections(d, "Image"),
            new[]
            {
                B("Game"),
                B("Vivid", "Image.Saturation", "1.45", "Image.Contrast", "1.2", "Image.Exposure", "0.15", "Image.Bloom", "1.4"),
                B("Bleak", "Image.Saturation", "0.45", "Image.Contrast", "1.2", "Image.Temperature", "-18", "Image.Tint", "4",
                    "Image.Exposure", "-0.25", "Image.Bloom", "0.6"),
                B("Plague", "Image.Saturation", "0.7", "Image.Tint", "-28", "Image.Temperature", "-8", "Image.Contrast", "1.12",
                    "Image.ChromaticAberration", "3.5", "Image.Exposure", "-0.1"),
                B("Old photo", "Image.Saturation", "0.12", "Image.Temperature", "38", "Image.Tint", "6", "Image.Contrast", "1.15",
                    "Image.Bloom", "1.6", "Image.ChromaticAberration", "3"),
                B("Noir", "Image.Saturation", "0", "Image.Contrast", "1.45", "Image.Exposure", "-0.2", "Image.Bloom", "1.2")
            });
    }
}
