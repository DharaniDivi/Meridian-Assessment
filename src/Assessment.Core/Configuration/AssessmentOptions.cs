namespace Assessment.Core.Configuration;

public sealed class AssessmentOptions
{
    public const string SectionName = "Assessment";

    /// <summary>Assessment platform base URL (no trailing slash).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key starting with sa_.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Local directory for cached dataset files.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Max retry attempts on HTTP 429/503.</summary>
    public int MaxRetries { get; set; } = 10;

    /// <summary>Delay between batch downloads (ms) to avoid rate limits.</summary>
    public int BatchDelayMs { get; set; } = 750;
}
