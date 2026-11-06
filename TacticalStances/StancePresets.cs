using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// User presets of every stance pose slider (all "Position X/Y/Z" and
/// "Rotation X/Y/Z" entries: rifle and pistol stances, canted aim, lean
/// swap). Drawn in the F12 config manager as a dropdown plus Apply / Save
/// New / Save / Rename / Delete / Reload. Picking a preset in the dropdown
/// only selects it — nothing changes until Apply is pressed, so a preset
/// can be renamed or deleted without touching the current pose.
///
/// Stored as plain text next to the config file so presets can be backed
/// up or shared:
///   [Preset Name]
///   c - Low Ready|Position X|0.0332
/// </summary>
internal static class StancePresets
{
    private sealed class Preset
    {
        public string Name;
        public readonly Dictionary<string, float> Values = new Dictionary<string, float>();
    }

    private static readonly List<Preset> _presets = new List<Preset>();
    private static ConfigFile _config;
    private static ConfigEntry<string> _lastApplied;
    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "com.devin.tacticalstances.presets.txt");

    // F12 UI state.
    private static string _selected;
    private static string _nameField = "";
    private static bool _dropdownOpen;
    private static bool _confirmDelete;
    private static string _status = "";

    internal static void Bind(ConfigFile config)
    {
        _config = config;
        _lastApplied = config.Bind("m - Presets", "Presets", "",
            new ConfigDescription(
                "Save the current stance pose sliders (Position/Rotation of every stance) as a named preset, and apply, rename or delete presets. " +
                "Selecting a preset does nothing until Apply is pressed. Stored in BepInEx/config/com.devin.tacticalstances.presets.txt. " +
                "Shows the last applied preset.",
                null,
                new ConfigurationManagerAttributes { CustomDrawer = Draw, HideDefaultButton = true, HideSettingName = true }));
        Load();
        _selected = _presets.Any(p => p.Name == _lastApplied.Value) ? _lastApplied.Value : _presets.FirstOrDefault()?.Name;
    }

    // Every pose slider this plugin owns, keyed "section|key".
    private static IEnumerable<KeyValuePair<string, ConfigEntry<float>>> PoseEntries()
    {
        foreach (var kv in _config)
        {
            string key = kv.Key.Key;
            if (kv.Value is ConfigEntry<float> f &&
                (key.StartsWith("Position ", StringComparison.Ordinal) || key.StartsWith("Rotation ", StringComparison.Ordinal)))
                yield return new KeyValuePair<string, ConfigEntry<float>>(kv.Key.Section + "|" + key, f);
        }
    }

    private static Preset Find(string name) => _presets.FirstOrDefault(p => p.Name == name);

    private static string CleanName(string name)
    {
        name = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('[', '(').Replace(']', ')').Trim();
        return name.Length > 40 ? name.Substring(0, 40).Trim() : name;
    }

    private static void Capture(Preset preset)
    {
        preset.Values.Clear();
        foreach (var kv in PoseEntries())
            preset.Values[kv.Key] = kv.Value.Value;
    }

    private static int Apply(Preset preset)
    {
        int applied = 0;
        // One config save for the whole preset instead of one per slider.
        bool autoSave = _config.SaveOnConfigSet;
        _config.SaveOnConfigSet = false;
        try
        {
            foreach (var kv in PoseEntries().ToList())
            {
                if (preset.Values.TryGetValue(kv.Key, out float v))
                {
                    kv.Value.Value = v;
                    applied++;
                }
            }
            _lastApplied.Value = preset.Name;
        }
        finally
        {
            _config.SaveOnConfigSet = autoSave;
            _config.Save();
        }
        return applied;
    }

    private static void Load()
    {
        _presets.Clear();
        try
        {
            if (!File.Exists(FilePath))
                return;
            Preset current = null;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string name = CleanName(line.Substring(1, line.Length - 2));
                    current = name.Length == 0 || Find(name) != null ? null : new Preset { Name = name };
                    if (current != null)
                        _presets.Add(current);
                    continue;
                }
                if (current == null)
                    continue;
                int cut = line.LastIndexOf('|');
                if (cut > 0 && float.TryParse(line.Substring(cut + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    current.Values[line.Substring(0, cut)] = v;
            }
        }
        catch (Exception e)
        {
            TacticalStancesPlugin.Log.LogError("Couldn't read stance presets: " + e.Message);
        }
    }

    private static bool SaveFile()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Tactical Stances presets. Section|Setting|Value, one preset per [Name] block.");
            foreach (Preset p in _presets)
            {
                sb.AppendLine();
                sb.AppendLine("[" + p.Name + "]");
                foreach (var kv in p.Values.OrderBy(k => k.Key, StringComparer.Ordinal))
                    sb.AppendLine(kv.Key + "|" + kv.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            File.WriteAllText(FilePath, sb.ToString());
            return true;
        }
        catch (Exception e)
        {
            TacticalStancesPlugin.Log.LogError("Couldn't save stance presets: " + e.Message);
            _status = "Couldn't write the presets file: " + e.Message;
            return false;
        }
    }

    // ---- F12 drawer -------------------------------------------------------

    // Row captions never wrap ("Preset" was breaking onto two lines), and
    // share one width so the dropdown and name box line up.
    private const float RowLabelWidth = 80f;
    private static GUIStyle _rowLabel;
    private static GUIStyle RowLabel =>
        _rowLabel ??= new GUIStyle(GUI.skin.label) { wordWrap = false, clipping = TextClipping.Overflow };

    private static void Draw(ConfigEntryBase entry)
    {
        GUILayout.BeginVertical();

        // Dropdown: selecting only picks — Apply does the work.
        GUILayout.BeginHorizontal();
        GUILayout.Label("Preset", RowLabel, GUILayout.Width(RowLabelWidth));
        string shown = _selected ?? (_presets.Count == 0 ? "(no presets yet)" : "(select)");
        if (GUILayout.Button(shown + (_dropdownOpen ? "  ▲" : "  ▼"), GUILayout.ExpandWidth(true)))
            _dropdownOpen = !_dropdownOpen && _presets.Count > 0;
        GUILayout.EndHorizontal();

        if (_dropdownOpen)
        {
            foreach (Preset p in _presets.ToList())
            {
                string label = (p.Name == _selected ? "● " : "   ") + p.Name + (p.Name == _lastApplied.Value ? "   (applied)" : "");
                if (GUILayout.Button(label))
                {
                    _selected = p.Name;
                    _nameField = p.Name;
                    _dropdownOpen = false;
                    _confirmDelete = false;
                    _status = "";
                }
            }
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", RowLabel, GUILayout.Width(RowLabelWidth));
        _nameField = GUILayout.TextField(_nameField ?? "", 40, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();

        Preset selected = Find(_selected);
        string typed = CleanName(_nameField);

        GUILayout.BeginHorizontal();
        bool was = GUI.enabled;

        GUI.enabled = was && selected != null;
        if (GUILayout.Button("Apply"))
        {
            int n = Apply(selected);
            _status = $"Applied \"{selected.Name}\" ({n} settings).";
            _confirmDelete = false;
        }

        GUI.enabled = was && typed.Length > 0;
        if (GUILayout.Button("Save New"))
        {
            if (Find(typed) != null)
            {
                _status = $"\"{typed}\" already exists. Select it and press Save to overwrite.";
            }
            else
            {
                var p = new Preset { Name = typed };
                Capture(p);
                _presets.Add(p);
                if (SaveFile())
                {
                    _selected = typed;
                    _status = $"Saved current pose as \"{typed}\".";
                }
            }
            _confirmDelete = false;
        }

        GUI.enabled = was && selected != null;
        if (GUILayout.Button("Save"))
        {
            Capture(selected);
            if (SaveFile())
                _status = $"Overwrote \"{selected.Name}\" with the current pose.";
            _confirmDelete = false;
        }

        GUI.enabled = was && selected != null && typed.Length > 0 && typed != selected?.Name;
        if (GUILayout.Button("Rename"))
        {
            if (Find(typed) != null)
            {
                _status = $"\"{typed}\" already exists.";
            }
            else
            {
                string old = selected.Name;
                selected.Name = typed;
                if (SaveFile())
                {
                    if (_lastApplied.Value == old)
                        _lastApplied.Value = typed;
                    _selected = typed;
                    _status = $"Renamed \"{old}\" to \"{typed}\".";
                }
            }
            _confirmDelete = false;
        }

        GUI.enabled = was && selected != null;
        if (GUILayout.Button(_confirmDelete ? "Confirm Delete" : "Delete"))
        {
            if (!_confirmDelete)
            {
                _confirmDelete = true;
                _status = $"Press Confirm Delete to remove \"{selected.Name}\".";
            }
            else
            {
                _presets.Remove(selected);
                if (SaveFile())
                {
                    if (_lastApplied.Value == selected.Name)
                        _lastApplied.Value = "";
                    _status = $"Deleted \"{selected.Name}\".";
                    _selected = _presets.FirstOrDefault()?.Name;
                    _nameField = _selected ?? "";
                }
                _confirmDelete = false;
            }
        }

        GUI.enabled = was;
        if (GUILayout.Button("Reload"))
        {
            Load();
            if (Find(_selected) == null)
                _selected = _presets.FirstOrDefault()?.Name;
            _status = $"Loaded {_presets.Count} preset(s) from file.";
            _confirmDelete = false;
        }

        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_status))
            GUILayout.Label(_status);

        GUILayout.EndVertical();
    }
}

/// <summary>Read by BepInEx.ConfigurationManager by field name.</summary>
internal sealed class ConfigurationManagerAttributes
{
    public Action<ConfigEntryBase> CustomDrawer;
    public bool? HideDefaultButton;
    public bool? HideSettingName;
}
