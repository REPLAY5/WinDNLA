using System.Collections.Concurrent;
using System.Diagnostics;
using WinDNLA.Core.Models;

namespace WinDNLA.Dlna;

public sealed class SessionTracker
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();

    public event EventHandler? SessionsChanged;

    public IReadOnlyList<ClientSessionInfo> GetSessions() =>
        _sessions.Values.Select(s => s.ToInfo()).OrderByDescending(s => s.StartedAt).ToList();

    public StreamSession Begin(string clientIp, string filePath, string fileName, bool isTranscoding)
    {
        var session = new StreamSession(this, clientIp, filePath, fileName, isTranscoding);
        _sessions[session.SessionId] = session;
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return session;
    }

    internal void End(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
            SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void NotifyChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class StreamSession : IDisposable
{
    private readonly SessionTracker _tracker;
    private long _bytes;
    private long _windowBytes;
    private readonly Stopwatch _window = Stopwatch.StartNew();
    private double _speedMbit;
    private bool _disposed;

    public StreamSession(SessionTracker tracker, string clientIp, string filePath, string fileName, bool isTranscoding)
    {
        _tracker = tracker;
        SessionId = Guid.NewGuid().ToString("N");
        ClientIp = clientIp;
        FilePath = filePath;
        FileName = fileName;
        IsTranscoding = isTranscoding;
        StartedAt = DateTimeOffset.Now;
    }

    public string SessionId { get; }
    public string ClientIp { get; }
    public string FilePath { get; }
    public string FileName { get; }
    public bool IsTranscoding { get; }
    public DateTimeOffset StartedAt { get; }

    public void AddBytes(int count)
    {
        Interlocked.Add(ref _bytes, count);
        Interlocked.Add(ref _windowBytes, count);
        if (_window.ElapsedMilliseconds >= 1000)
        {
            var elapsed = _window.Elapsed.TotalSeconds;
            var window = Interlocked.Exchange(ref _windowBytes, 0);
            _window.Restart();
            if (elapsed > 0)
                _speedMbit = window * 8.0 / 1_000_000.0 / elapsed;
            _tracker.NotifyChanged();
        }
    }

    public ClientSessionInfo ToInfo() => new()
    {
        SessionId = SessionId,
        ClientIp = ClientIp,
        FilePath = FilePath,
        FileName = FileName,
        IsTranscoding = IsTranscoding,
        StartedAt = StartedAt,
        SpeedMbitPerSec = Math.Round(_speedMbit, 2)
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.End(SessionId);
    }
}
