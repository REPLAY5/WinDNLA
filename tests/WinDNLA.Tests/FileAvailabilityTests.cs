using System.Text;
using WinDNLA.Core.Services;

namespace WinDNLA.Tests;

public class FileAvailabilityTests
{
    [Fact]
    public void Ready_file_is_indexable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windnla-ready-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(path, "video-bytes");
        try
        {
            Assert.True(FileAvailability.IsReadyForIndexing(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Locked_for_write_is_not_indexable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windnla-busy-{Guid.NewGuid():N}.mp4");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        writer.Write(Encoding.UTF8.GetBytes("downloading..."));
        writer.Flush();

        try
        {
            Assert.False(FileAvailability.IsReadyForIndexing(path));
        }
        finally
        {
            writer.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Shared_write_lock_is_not_indexable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windnla-dl-{Guid.NewGuid():N}.mkv");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Encoding.UTF8.GetBytes("partial content"));
        writer.Flush();

        try
        {
            Assert.False(FileAvailability.IsReadyForIndexing(path));
        }
        finally
        {
            writer.Dispose();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("movie.mp4.part")]
    [InlineData("movie.mkv.crdownload")]
    [InlineData("movie.avi.download")]
    [InlineData("movie.!qb")]
    public void Incomplete_extensions_are_rejected(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, "x");
        try
        {
            Assert.False(FileAvailability.IsReadyForIndexing(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unstable_size_is_detected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"windnla-grow-{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(path, "a");
        try
        {
            var grow = Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    await File.AppendAllTextAsync(path, new string('x', 1024));
                    await Task.Delay(30);
                }
            });

            await Task.Delay(50);
            var stable = await FileAvailability.IsSizeStableAsync(path, settleMs: 80);
            Assert.False(stable);
            await grow;
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
