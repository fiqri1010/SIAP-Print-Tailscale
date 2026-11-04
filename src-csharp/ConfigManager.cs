using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SiapPrintServer;

public static class ConfigManager
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SIAP-Print-Tailscale"
    );

    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
    private static readonly string LogsPath = Path.Combine(DataDir, "logs.json");

    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    public static event Action<PrintLogItem>? LogAdded;

    static ConfigManager()
    {
        try
        {
            if (!Directory.Exists(DataDir))
            {
                Directory.CreateDirectory(DataDir);
            }
        }
        catch { }
    }

    public static AppConfig LoadConfig()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { }

            var defaultConfig = new AppConfig();
            SaveConfig(defaultConfig);
            return defaultConfig;
        }
    }

    public static void SaveConfig(AppConfig config)
    {
        lock (FileLock)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                string json = JsonSerializer.Serialize(config, IndentedJsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }

    public static List<PrintLogItem> LoadLogs()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(LogsPath))
                {
                    string json = File.ReadAllText(LogsPath);
                    var logs = JsonSerializer.Deserialize<List<PrintLogItem>>(json);
                    if (logs != null) return logs;
                }
            }
            catch { }

            return new List<PrintLogItem>();
        }
    }

    public static void AddLog(PrintLogItem item)
    {
        lock (FileLock)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                var logs = LoadLogs();
                logs.Insert(0, item);
                if (logs.Count > 100)
                {
                    logs = logs.GetRange(0, 100);
                }

                string json = JsonSerializer.Serialize(logs, IndentedJsonOptions);
                File.WriteAllText(LogsPath, json);
            }
            catch { }
        }

        try
        {
            LogAdded?.Invoke(item);
        }
        catch { }
    }

    public static void ClearLogs()
    {
        lock (FileLock)
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                File.WriteAllText(LogsPath, "[]");
            }
            catch { }
        }
    }
}
