namespace WinDNLA.Core.Models;

public sealed class AppSettings
{
    public string FriendlyName { get; set; } = "WinDNLA";
    public int HttpPort { get; set; } = 8200;
    public List<string> LibraryRoots { get; set; } = [];
    public int AutoRescanSeconds { get; set; } = 30;
    public bool RunAtStartup { get; set; } = true;
    public bool TranscodingEnabled { get; set; } = true;
    public bool ServerAutoStart { get; set; } = true;
    public string? FfmpegPathOverride { get; set; }
    /// <summary>IPv4 addresses excluded from SSDP. Empty = all interfaces enabled.</summary>
    public List<string> DisabledNetworkAddresses { get; set; } = [];
    public List<TranscodeRule> TranscodeRules { get; set; } = TranscodeRule.CreateDefaults();
}

public sealed class TranscodeRule
{
    /// <summary>Extensions including dot, e.g. .avi. Empty = any.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>If true, match when video codec is NOT in AllowedCodecs.</summary>
    public bool MatchNonAllowedCodecs { get; set; }

    /// <summary>Allowed codecs when MatchNonAllowedCodecs is true (normalized lowercase).</summary>
    public List<string> AllowedCodecs { get; set; } = ["h264", "avc", "hevc", "h265"];

    public bool Enabled { get; set; } = true;

    public static List<TranscodeRule> CreateDefaults() =>
    [
        new()
        {
            Extensions = [".avi"],
            MatchNonAllowedCodecs = false,
            Enabled = true
        },
        new()
        {
            Extensions = [],
            MatchNonAllowedCodecs = true,
            AllowedCodecs = ["h264", "avc", "hevc", "h265"],
            Enabled = true
        }
    ];
}
