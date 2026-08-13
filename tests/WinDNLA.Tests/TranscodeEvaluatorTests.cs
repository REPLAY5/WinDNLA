using WinDNLA.Core.Models;
using WinDNLA.Core.Services;

namespace WinDNLA.Tests;

public class TranscodeEvaluatorTests
{
    [Fact]
    public void Avi_extension_rule_triggers_transcode()
    {
        var settings = new AppSettings { TranscodingEnabled = true };
        Assert.True(TranscodeEvaluator.NeedsTranscode(settings, @"C:\v\movie.avi", "mpeg4"));
    }

    [Fact]
    public void H264_mp4_does_not_need_transcode_by_default_codec_rule_when_ext_empty()
    {
        var settings = new AppSettings
        {
            TranscodingEnabled = true,
            TranscodeRules =
            [
                new TranscodeRule
                {
                    Extensions = [],
                    MatchNonAllowedCodecs = true,
                    AllowedCodecs = ["h264", "hevc"],
                    Enabled = true
                }
            ]
        };
        Assert.False(TranscodeEvaluator.NeedsTranscode(settings, @"C:\v\movie.mp4", "h264"));
        Assert.False(TranscodeEvaluator.NeedsTranscode(settings, @"C:\v\movie.mp4", "avc1"));
        Assert.True(TranscodeEvaluator.NeedsTranscode(settings, @"C:\v\movie.mp4", "mpeg4"));
        Assert.True(TranscodeEvaluator.NeedsTranscode(settings, @"C:\v\movie.mp4", "xvid"));
    }

    [Fact]
    public void Disabled_global_flag_skips_all_rules()
    {
        var settings = new AppSettings { TranscodingEnabled = false };
        Assert.False(TranscodeEvaluator.NeedsTranscode(settings, @"C:\v\movie.avi", "mpeg4"));
    }

    [Theory]
    [InlineData(".mp4", true)]
    [InlineData(".mkv", true)]
    [InlineData(".txt", false)]
    [InlineData(".jpg", false)]
    public void IsVideoFile(string ext, bool expected) =>
        Assert.Equal(expected, TranscodeEvaluator.IsVideoFile("file" + ext));

    [Fact]
    public void ProtocolInfo_differs_for_transcode()
    {
        var direct = TranscodeEvaluator.ProtocolInfo("a.mp4", false);
        var tx = TranscodeEvaluator.ProtocolInfo("a.avi", true);
        Assert.Contains("video/mp4", direct);
        Assert.Contains("video/mpeg", tx);
        Assert.Contains("CI=1", tx);
        Assert.Contains("OP=10", tx);
        Assert.Contains("FLAGS=41700000", tx);
        Assert.Contains("OP=01", direct);
        Assert.DoesNotContain("FLAGS=41700000", direct);
    }
}
