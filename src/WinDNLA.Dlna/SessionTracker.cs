using System.Collections.Concurrent;
using System.Diagnostics;
using WinDNLA.Core.Models;
using WinDNLA.Core.Services;

namespace WinDNLA.Dlna;

public sealed class SessionTracker : IDisposable
{
    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new();
    private readonly LibraryRepository _repo;
    private readonly object _idleTimerLock = new();
    private Timer? _idleTimer;
    private bool _disposed;

    public SessionTracker(LibraryRepository repo) => _repo = repo;

    public event EventHandler? SessionsChanged;

    public IReadOnlyList<ClientSessionInfo> GetSessions() =>
        _sessions.Values
            .Select(s => EnrichFromDb(s.ToInfo()))
            .OrderByDescending(s => s.StartedAt)
            .ToList();

    public StreamSession Begin(string clientIp, string filePath, string fileName, bool isTranscoding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = new StreamSession(this, clientIp, filePath, fileName, isTranscoding);
        _sessions[session.SessionId] = session;
        EnsureIdleTimer();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public StreamSession Begin(string clientIp, VideoRecord video, bool isTranscoding) =>
        Begin(
            clientIp,
            video.Path,
            string.IsNullOrWhiteSpace(video.Title) ? Path.GetFileName(video.Path) : video.Title,
            isTranscoding);

    internal void End(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out _))
        {
            if (_sessions.IsEmpty)
                StopIdleTimer();
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void NotifyChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    private void EnsureIdleTimer()
    {
        lock (_idleTimerLock)
        {
            if (_disposed) return;
            _idleTimer ??= new Timer(_ => TickIdleSpeeds(), null, 1000, 1000);
        }
    }

    private void StopIdleTimer()
    {
        lock (_idleTimerLock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private void TickIdleSpeeds()
    {
        var changed = false;
        foreach (var session in _sessions.Values)
            changed |= session.TryDecayIdleSpeed();
        if (changed)
            NotifyChanged();
    }

    private ClientSessionInfo EnrichFromDb(ClientSessionInfo info)
    {
        var video = _repo.GetVideoByPath(info.FilePath);
        if (video is null) return info;

        info.DurationSeconds = video.DurationSeconds;
        info.SizeBytes = video.Size;
        info.VideoCodec = video.VideoCodec;
        info.Width = video.Width;
        info.Height = video.Height;
        if (string.IsNullOrWhiteSpace(info.FileName))
            info.FileName = video.Title;
        return info;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIdleTimer();
    }
}

public sealed class StreamSession : IDisposable
{
    private const int IdleSpeedMs = 1000;

    private readonly SessionTracker _tracker;
    private long _bytes;
    private long _windowBytes;
    private readonly Stopwatch _window = Stopwatch.StartNew();
    private long _lastBytesTimestamp = Stopwatch.GetTimestamp();
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
        Volatile.Write(ref _lastBytesTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Add(ref _bytes, count);
        Interlocked.Add(ref _windowBytes, count);
        if (_window.ElapsedMilliseconds >= IdleSpeedMs)
        {
            var elapsed = _window.Elapsed.TotalSeconds;
            var window = Interlocked.Exchange(ref _windowBytes, 0);
            _window.Restart();
            if (elapsed > 0)
                _speedMbit = window * 8.0 / 1_000_000.0 / elapsed;
            _tracker.NotifyChanged();
        }
    }

    /// <summary>
    /// When the client pauses and stops reading, no more AddBytes calls arrive.
    /// Decay the displayed speed to 0 after a full idle window.
    /// </summary>
    internal bool TryDecayIdleSpeed()
    {
        if (_speedMbit == 0) return false;
        if (IdleMilliseconds() < IdleSpeedMs) return false;
        _speedMbit = 0;
        Interlocked.Exchange(ref _windowBytes, 0);
        _window.Restart();
        return true;
    }

    public ClientSessionInfo ToInfo() => new()
    {
        SessionId = SessionId,
        ClientIp = ClientIp,
        FilePath = FilePath,
        FileName = FileName,
        IsTranscoding = IsTranscoding,
        StartedAt = StartedAt,
        SpeedMbitPerSec = Math.Round(EffectiveSpeedMbit(), 2)
    };

    private double EffectiveSpeedMbit() =>
        IdleMilliseconds() >= IdleSpeedMs ? 0 : _speedMbit;

    private double IdleMilliseconds() =>
        Stopwatch.GetElapsedTime(Volatile.Read(ref _lastBytesTimestamp)).TotalMilliseconds;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tracker.End(SessionId);
    }
}
