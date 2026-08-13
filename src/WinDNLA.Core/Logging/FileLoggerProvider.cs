using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WinDNLA.Core.Logging;

/// <summary>
/// Background file logger: %LocalAppData%\WinDNLA\logs\windnla-yyyy-MM-dd.log
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetainDays = 7;
    private const int QueueCapacity = 50_000;

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly Thread _thread;
    private StreamWriter? _writer;
    private string? _openPath;
    private int _disposed;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOldLogs(directory);
        _thread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "WinDNLA-FileLog",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    internal void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disposed != 0) return;
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var line = exception is null
            ? $"{stamp} [{Level(level)}] {category} {message}"
            : $"{stamp} [{Level(level)}] {category} {message}{Environment.NewLine}{exception}";
        if (!_queue.TryAdd(line))
            TryAdd($"… dropped log line (queue full) {category}");
    }

    /// <summary>Direct write when DI/logger may be unavailable (crash path).</summary>
    public static void WriteEmergency(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            Directory.CreateDirectory(AppPaths.LogsDir);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [FTL] {message}{Environment.NewLine}";
            File.AppendAllText(AppPaths.CurrentLogFile, line);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _queue.CompleteAdding(); } catch { /* ignore */ }
        try { _thread.Join(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        try { _writer?.Flush(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _queue.Dispose();
    }

    private void TryAdd(string line)
    {
        try { _queue.TryAdd(line); } catch { /* ignore */ }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    EnsureWriter();
                    _writer!.WriteLine(line);
                    _writer.Flush();
                }
                catch
                {
                    try { _writer?.Dispose(); } catch { /* ignore */ }
                    _writer = null;
                    _openPath = null;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // completed
        }
        finally
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
        }
    }

    private void EnsureWriter()
    {
        var path = Path.Combine(_directory, $"windnla-{DateTime.Now:yyyy-MM-dd}.log");
        if (_writer is not null && string.Equals(_openPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        try { _writer?.Flush(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
        _openPath = path;
    }

    private static void PruneOldLogs(string directory)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetainDays);
            foreach (var file in Directory.GetFiles(directory, "windnla-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "DBG"
    };
}

internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = ShortCategory(category);
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        _provider.Enqueue(logLevel, _category, formatter(state, exception), exception);
    }

    private static string ShortCategory(string category)
    {
        var last = category.LastIndexOf('.');
        return last >= 0 && last < category.Length - 1 ? category[(last + 1)..] : category;
    }
}
