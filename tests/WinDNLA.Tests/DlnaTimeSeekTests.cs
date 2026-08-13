using WinDNLA.Dlna;

namespace WinDNLA.Tests;

public class DlnaTimeSeekTests
{
    [Theory]
    [InlineData("npt=90-", 90.0, null)]
    [InlineData("npt=5.25-", 5.25, null)]
    [InlineData("npt=0:01:30.000-", 90.0, null)]
    [InlineData("npt=1:02:03.500-1:10:00.000", 3723.5, 4200.0)]
    [InlineData(" npt=00:00:10.000- ", 10.0, null)]
    public void TryParseRequest_parses_npt(string header, double start, double? end)
    {
        Assert.True(DlnaTimeSeek.TryParseRequest(header, out var s, out var e));
        Assert.Equal(start, s, 3);
        if (end is null)
            Assert.Null(e);
        else
            Assert.Equal(end.Value, e!.Value, 3);
    }

    [Fact]
    public void FormatResponse_includes_duration()
    {
        var r = DlnaTimeSeek.FormatResponse(90, null, 600);
        Assert.Equal("npt=90.000-600.000/600.000", r);
    }

    [Fact]
    public void FormatAvailableRange_is_cleartext_not_dtcp()
    {
        Assert.Equal("0 npt=0.000-12.500", DlnaTimeSeek.FormatAvailableRange(12.5));
        Assert.StartsWith("0 ", DlnaTimeSeek.FormatAvailableRange(0));
    }

    [Fact]
    public void FormatMediaInfoSec_is_milliseconds()
    {
        Assert.Equal("SEC_Duration=12500;", DlnaTimeSeek.FormatMediaInfoSec(12.5));
    }
}
