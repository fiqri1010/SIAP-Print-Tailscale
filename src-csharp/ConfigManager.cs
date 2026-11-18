using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace SiapPrintServer;

public static class ConfigManager
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SIAP-Print-Tailscale"
    );

    private static readonly string ConfigPath = Path.Combine(DataDir, "config.json");
    private static readonly string LogsPath = Path.Combine(DataDir, "logs.json");

    private const string LogsMutexName = @"Global\SIAP_Print_Tailscale_Logs_Mutex";
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    public static event Action<PrintLogItem>? LogAdded;

    public static string GetDataDir() => DataDir;
    public static string GetLogsPath() => LogsPath;

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

    private static IDisposable AcquireLogsMutex(int timeoutMs = 3000)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, LogsMutexName);
        }
        catch
        {
            // Fallback jika pembuatan Global Mutex dibatasi oleh kebijakan OS
            mutex = new Mutex(false, @"Local\SIAP_Print_Tailscale_Logs_Mutex");
        }

        bool hasHandle = false;
        try
        {
            hasHandle = mutex.WaitOne(timeoutMs, false);
        }
        catch (AbandonedMutexException)
        {
            hasHandle = true;
        }
        return new MutexReleaser(mutex, hasHandle);
    }

    private sealed class MutexReleaser : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _hasHandle;
        private bool _disposed;

        public MutexReleaser(Mutex mutex, bool hasHandle)
        {
            _mutex = mutex;
            _hasHandle = hasHandle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hasHandle)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            _mutex.Dispose();
        }
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

    public static DateTime GetLogsLastWriteUtc()
    {
        try
        {
            if (File.Exists(LogsPath))
            {
                return File.GetLastWriteTimeUtc(LogsPath);
            }
        }
        catch { }
        return DateTime.MinValue;
    }

    public static List<PrintLogItem> LoadLogs()
    {
        using var releaser = AcquireLogsMutex();
        return LoadLogsInternal();
    }

    private static List<PrintLogItem> LoadLogsInternal()
    {
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                if (File.Exists(LogsPath))
                {
                    using var stream = new FileStream(LogsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (stream.Length == 0) return new List<PrintLogItem>();
                    var logs = JsonSerializer.Deserialize<List<PrintLogItem>>(stream);
                    if (logs != null) return logs;
                }
                return new List<PrintLogItem>();
            }
            catch (IOException) when (retry < 2)
            {
                Thread.Sleep(50);
            }
            catch { }
        }
        return new List<PrintLogItem>();
    }

    public static void AddLog(PrintLogItem item)
    {
        using var releaser = AcquireLogsMutex();
        try
        {
            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);

            List<PrintLogItem> logs = new();
            bool readSuccess = false;

            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    if (File.Exists(LogsPath))
                    {
                        using var stream = new FileStream(LogsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (stream.Length > 0)
                        {
                            var parsed = JsonSerializer.Deserialize<List<PrintLogItem>>(stream);
                            if (parsed != null)
                            {
                                logs = parsed;
                                readSuccess = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        logs = new List<PrintLogItem>();
                        readSuccess = true;
                        break;
                    }
                }
                catch (IOException) when (retry < 2)
                {
                    Thread.Sleep(50);
                }
                catch { }
            }

            if (!readSuccess)
            {
                // Bila file ada tetapi gagal dibaca karena kunci sementara, coba baca ulang
                if (File.Exists(LogsPath) && new FileInfo(LogsPath).Length > 10)
                {
                    Thread.Sleep(100);
                    logs = LoadLogsInternal();
                }
                else
                {
                    logs = new List<PrintLogItem>();
                }
            }

            logs.Insert(0, item);
            if (logs.Count > 100)
            {
                logs = logs.GetRange(0, 100);
            }

            // Tulis secara aman menggunakan file sementara agar proses lain tidak membaca file setengah jadi
            string tempPath = Path.Combine(DataDir, $"logs_{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(stream, logs, IndentedJsonOptions);
            }
            File.Move(tempPath, LogsPath, true);
        }
        catch { }

        try
        {
            LogAdded?.Invoke(item);
        }
        catch { }
    }

    public static void ClearLogs()
    {
        using var releaser = AcquireLogsMutex();
        try
        {
            if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
            File.WriteAllText(LogsPath, "[]");
        }
        catch { }
    }
}
