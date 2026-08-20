using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

internal static class APConfig
{
    private const string FileName = "ap_config.json";

    private static readonly object _lock = new object();
    private static bool _loaded = false;

    private static readonly HashSet<string> _collectibles = new HashSet<string>();
    private static readonly HashSet<string> _checkpoints = new HashSet<string>();

    private static string _configPath;
    private static BepInEx.Logging.ManualLogSource _logger;

    public static void Init(BepInEx.Logging.ManualLogSource logger)
    {
        lock (_lock)
        {
            if (_logger == null) _logger = logger;
            if (_loaded) return;

            _configPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                FileName);

            Load();
            _loaded = true;
        }
    }

    public static bool IsCollectibleCollected(string name)
    {
        lock (_lock) return _collectibles.Contains(name);
    }

    public static bool IsCheckpointUnlocked(string name)
    {
        lock (_lock) return _checkpoints.Contains(name);
    }

    public static void MarkCollectibleCollected(string name)
    {
        lock (_lock)
        {
            if (_collectibles.Add(name)) Save();
        }
    }

    public static void MarkCheckpointUnlocked(string name)
    {
        lock (_lock)
        {
            if (_checkpoints.Add(name)) Save();
        }
    }

    // ==========================================================================
    // Lecture
    // ==========================================================================
    private static void Load()
    {
        _collectibles.Clear();
        _checkpoints.Clear();

        if (!File.Exists(_configPath))
        {
            _logger?.LogInfo($"ApConfig: aucun '{FileName}' trouve, demarrage avec une progression vide.");
            return;
        }

        try
        {
            string json = File.ReadAllText(_configPath, Encoding.UTF8);
            foreach (var name in ExtractStringArray(json, "collectibles"))
                _collectibles.Add(name);
            foreach (var name in ExtractStringArray(json, "checkpoints"))
                _checkpoints.Add(name);

            _logger?.LogInfo($"ApConfig: '{FileName}' charge ({_collectibles.Count} collectible(s), {_checkpoints.Count} checkpoint(s)).");
        }
        catch (Exception e)
        {
            _logger?.LogWarning($"ApConfig: echec de lecture de '{_configPath}' ({e.Message}), progression consideree vide.");
        }
    }

    // Extraction minimaliste d'un tableau de chaines JSON du type
    // "cle": ["a", "b", "c"], sans dependance a une lib JSON externe.
    private static List<string> ExtractStringArray(string json, string key)
    {
        var result = new List<string>();

        string marker = "\"" + key + "\"";
        int keyIndex = json.IndexOf(marker, StringComparison.Ordinal);
        if (keyIndex < 0) return result;

        int arrayStart = json.IndexOf('[', keyIndex);
        if (arrayStart < 0) return result;

        int arrayEnd = json.IndexOf(']', arrayStart);
        if (arrayEnd < 0) return result;

        string inner = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

        int i = 0;
        while (i < inner.Length)
        {
            int quoteStart = inner.IndexOf('"', i);
            if (quoteStart < 0) break;

            var sb = new StringBuilder();
            int j = quoteStart + 1;
            while (j < inner.Length && inner[j] != '"')
            {
                if (inner[j] == '\\' && j + 1 < inner.Length)
                {
                    j++;
                    switch (inner[j])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(inner[j]); break;
                    }
                }
                else
                {
                    sb.Append(inner[j]);
                }
                j++;
            }

            result.Add(sb.ToString());
            i = j + 1;
        }

        return result;
    }

    // ==========================================================================
    // Ecriture
    // ==========================================================================
    private static void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"collectibles\": [\n");
            AppendArray(sb, _collectibles);
            sb.Append("  ],\n");
            sb.Append("  \"checkpoints\": [\n");
            AppendArray(sb, _checkpoints);
            sb.Append("  ]\n");
            sb.Append("}\n");

            File.WriteAllText(_configPath, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception e)
        {
            _logger?.LogWarning($"ApConfig: echec d'ecriture de '{_configPath}' ({e.Message}).");
        }
    }

    private static void AppendArray(StringBuilder sb, HashSet<string> values)
    {
        int i = 0;
        foreach (var value in values)
        {
            sb.Append("    \"").Append(EscapeJson(value)).Append("\"");
            i++;
            sb.Append(i < values.Count ? ",\n" : "\n");
        }
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}