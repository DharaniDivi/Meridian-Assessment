using Assessment.Core.Configuration;
using Assessment.Core.Http;
using Assessment.Core.Models;
using Assessment.Core.Services;
using Assessment.Core.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Assessment.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class AssessmentController : ControllerBase
{
    private readonly AssessmentHttpClient _client;
    private readonly AssessmentOptions _options;
    private readonly Layer1Service _layer1;
    private readonly Layer2Service _layer2;
    private readonly Layer3Service _layer3;
    private readonly Layer4Service _layer4;
    private readonly SubmissionService _submission;
    private readonly KeyAcquisitionService _keys;

    public AssessmentController(
        AssessmentHttpClient client,
        IOptions<AssessmentOptions> options,
        Layer1Service layer1,
        Layer2Service layer2,
        Layer3Service layer3,
        Layer4Service layer4,
        SubmissionService submission,
        KeyAcquisitionService keys)
    {
        _client = client;
        _options = options.Value;
        _layer1 = layer1;
        _layer2 = layer2;
        _layer3 = layer3;
        _layer4 = layer4;
        _submission = submission;
        _keys = keys;
    }

    [HttpGet("config")]
    public IActionResult GetConfigStatus()
    {
        return Ok(new
        {
            baseUrlConfigured = !string.IsNullOrWhiteSpace(_options.BaseUrl),
            apiKeyConfigured = !string.IsNullOrWhiteSpace(_options.ApiKey),
            baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl)
                ? null
                : _options.BaseUrl.TrimEnd('/')
        });
    }

    [HttpGet("health")]
    public Task<HealthResult> GetHealth(CancellationToken cancellationToken)
        => _client.GetHealthAsync(cancellationToken);

    [HttpGet("time")]
    public async Task<IActionResult> GetTime(CancellationToken cancellationToken)
    {
        var result = await _client.GetTimeRemainingAsync(cancellationToken);
        return Ok(new
        {
            result.Success,
            result.StatusCode,
            remaining = result.Remaining?.ToString(@"hh\:mm\:ss"),
            remainingSeconds = result.Remaining.HasValue ? (int)result.Remaining.Value.TotalSeconds : (int?)null,
            result.Message,
            hint = result.Success
                ? null
                : "The assessment platform may not expose a public time endpoint. Note when you made your first authenticated call."
        });
    }

    [HttpGet("discover")]
    public Task<IReadOnlyList<string>> Discover(CancellationToken cancellationToken)
        => _client.DiscoverEndpointsAsync(cancellationToken);

    [HttpPost("layers/1/run")]
    public Task<Layer1Result> RunLayer1(CancellationToken cancellationToken)
        => _layer1.FetchAndHashAsync(cancellationToken);

    [HttpGet("layers/1/hash")]
    public async Task<ActionResult<object>> GetLayer1Hash(CancellationToken cancellationToken)
    {
        var hash = await _layer1.ComputeHashFromCacheAsync(cancellationToken);
        return hash is null ? NotFound(new { error = "No cached dataset" }) : Ok(new { hash });
    }

    [HttpPost("layers/2/run")]
    public Task<Layer2Result> RunLayer2(CancellationToken cancellationToken)
        => _layer2.DecryptDatasetAsync(cancellationToken);

    [HttpPost("layers/2/acquire-key")]
    public async Task<IActionResult> AcquireLayer2Key(CancellationToken cancellationToken)
    {
        var (key, source, error) = await _keys.AcquireKeyAsync(cancellationToken);
        var probes = await _keys.ProbeKeySourcesAsync(cancellationToken);
        var derived = await _keys.GetDerivedKeyCandidatesAsync(cancellationToken);
        return Ok(new
        {
            keyFound = key is not null,
            key,
            source,
            error,
            probes,
            derivedCandidates = derived.Select(d => new { d.Source, keyPreview = d.Key.Length > 12 ? d.Key[..8] + "..." : d.Key }).ToList()
        });
    }

    [HttpPost("layers/2/try-keys")]
    public Task<object> TryLayer2Keys(CancellationToken cancellationToken)
        => _layer2.TryKeyCandidatesAsync(cancellationToken);

    [HttpGet("layers/2/records")]
    public async Task<IActionResult> GetRecords(CancellationToken cancellationToken)
    {
        var records = await _layer2.LoadDecryptedRecordsAsync(cancellationToken);
        return Ok(records.Take(100));
    }

    [HttpPost("layers/3/run")]
    public Task<Layer3Result> RunLayer3(CancellationToken cancellationToken)
        => _layer3.FindHiddenAnswerAsync(cancellationToken);

    [HttpGet("layers/3/candidates")]
    public Task<IReadOnlyList<string>> GetLayer3Candidates(CancellationToken cancellationToken)
        => _layer3.GetAllCandidatesAsync(cancellationToken);

    [HttpPost("layers/4/run")]
    public Task<Layer4Result> RunLayer4(CancellationToken cancellationToken)
        => _layer4.GenerateAnalysisAsync(cancellationToken);

    [HttpPost("submit")]
    public async Task<IActionResult> Submit([FromBody] SubmissionRequest request, CancellationToken cancellationToken)
    {
        var result = await _submission.SubmitAsync(request.Type, request.Value, request.Notes, cancellationToken);
        if (!result.Success)
        {
            return StatusCode(result.StatusCode > 0 ? result.StatusCode : 400, result);
        }

        return Ok(result);
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> GetDiagnostics(CancellationToken cancellationToken)
    {
        var dataDir = Path.GetFullPath(_options.DataDirectory);
        var datasetPath = Path.Combine(dataDir, "dataset.bin");
        string? hexPreview = null;
        long? datasetSize = null;

        if (System.IO.File.Exists(datasetPath))
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(datasetPath, cancellationToken);
            datasetSize = bytes.Length;
            var take = Math.Min(32, bytes.Length);
            hexPreview = Convert.ToHexString(bytes.AsSpan(0, take)).ToLowerInvariant();
        }

        var keyPaths = new[]
        {
            "api/v1/decryption-key",
            "api/v1/layer2/key",
            "api/v1/key"
        };

        var probes = new List<object>();
        foreach (var path in keyPaths)
        {
            var (key, error, status) = await _client.TryGetKeyFromPathAsync(path, cancellationToken);
            probes.Add(new { path, status, hasKey = key is not null, error });
        }

        return Ok(new
        {
            dataDirectory = dataDir,
            datasetSize,
            datasetHexPreview = hexPreview,
            layer1Meta = await _layer1.ReadMetaAsync(cancellationToken),
            hashCandidates = await _layer1.GetHashCandidatesAsync(cancellationToken),
            etag = ReadDatasetEtag(),
            hasDecryptionKeyFile = System.IO.File.Exists(Path.Combine(dataDir, "decryption-key.txt")),
            hasSubmissionLayer1 = System.IO.File.Exists(Path.Combine(dataDir, "submission-content_hash.json")) ||
                                  System.IO.File.Exists(Path.Combine(dataDir, "submission-layer1.json")),
            hasDatasetHeaders = System.IO.File.Exists(Path.Combine(dataDir, "dataset-headers.json")),
            keyProbes = probes,
            validSubmissionTypes = AssessmentSubmissionTypes.All,
            nextStep = "Submit content_hash via POST /api/submit/layer1, then retry Layer 2."
        });
    }

    [HttpGet("submit/types")]
    public Task<IReadOnlyList<string>> GetSubmissionTypes(CancellationToken cancellationToken)
        => _client.DiscoverSubmissionTypesAsync(cancellationToken);

    [HttpGet("layers/1/candidates")]
    public async Task<IActionResult> GetLayer1HashCandidates(CancellationToken cancellationToken)
    {
        var candidates = await _layer1.GetHashCandidatesAsync(cancellationToken);
        return candidates.Count == 0
            ? NotFound(new { error = "Run Layer 1 first." })
            : Ok(new { candidates });
    }

    [HttpPost("submit/layer1")]
    public async Task<IActionResult> SubmitLayer1(CancellationToken cancellationToken)
    {
        var hash = await _layer1.GetSubmitHashAsync(cancellationToken);
        if (hash is null)
        {
            return BadRequest(new SubmissionResult(false, 400, "Run Layer 1 first to cache the dataset."));
        }

        var meta = await _layer1.ReadMetaAsync(cancellationToken);
        var result = await _submission.SubmitLayer1Async(hash, cancellationToken: cancellationToken);
        if (result.Success)
        {
            return Ok(new
            {
                result.Success,
                result.StatusCode,
                result.Message,
                submittedHash = hash,
                primaryFormat = meta?.PrimaryFormat
            });
        }

        var candidates = await _layer1.GetNamedHashCandidatesAsync(cancellationToken);
        return Ok(new
        {
            result.Success,
            result.StatusCode,
            result.Message,
            submittedHash = hash,
            primaryFormat = meta?.PrimaryFormat,
            alternateHashes = candidates.Values.Where(c => !string.Equals(c, hash, StringComparison.OrdinalIgnoreCase)).ToList(),
            namedCandidates = candidates,
            hint = "Try POST /api/submit/layer1/try-candidates to test all formats, or submit manually from namedCandidates."
        });
    }

    [HttpPost("submit/layer1/try-candidates")]
    public async Task<IActionResult> TrySubmitAllLayer1Candidates(CancellationToken cancellationToken)
    {
        var (success, winningHash, winningFormat, attempts) =
            await _layer1.TrySubmitAllCandidatesAsync(_submission, cancellationToken);

        return Ok(new
        {
            success,
            winningHash,
            winningFormat,
            attemptCount = attempts.Count,
            attempts
        });
    }

    [HttpPost("layers/1/rehash")]
    public Task<Layer1Result> RehashLayer1(CancellationToken cancellationToken)
        => _layer1.RehashFromCacheAsync(cancellationToken);

    [HttpGet("layers/1/meta")]
    public async Task<IActionResult> GetLayer1Meta(CancellationToken cancellationToken)
    {
        var meta = await _layer1.ReadMetaAsync(cancellationToken);
        return meta is null ? NotFound(new { error = "Run Layer 1 first." }) : Ok(meta);
    }

    private string? ReadDatasetEtag()
    {
        var path = Path.Combine(_options.DataDirectory, "dataset-headers.json");
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("ETag", out var etag))
            {
                return EtagParser.ExtractSha256Hex(etag.GetString());
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    [HttpGet("layers/2/hash")]
    public async Task<ActionResult<object>> GetLayer2Hash(CancellationToken cancellationToken)
    {
        var hash = await _layer2.ComputeDecryptedHashAsync(cancellationToken);
        return hash is null ? NotFound(new { error = "No decrypted data" }) : Ok(new { hash });
    }

    [HttpPost("submit/layer2")]
    public async Task<IActionResult> SubmitLayer2(CancellationToken cancellationToken)
    {
        var result = await _submission.SubmitLayer2Async(cancellationToken: cancellationToken);
        return result.Success
            ? Ok(result)
            : StatusCode(result.StatusCode > 0 ? result.StatusCode : 400, result);
    }

    [HttpPost("submit/layer4")]
    public async Task<IActionResult> SubmitLayer4(CancellationToken cancellationToken)
    {
        var analysis = await _layer4.GenerateAnalysisAsync(cancellationToken);
        if (!analysis.Success || string.IsNullOrWhiteSpace(analysis.Analysis))
        {
            return BadRequest(new SubmissionResult(false, 400, analysis.Message ?? "No analysis generated."));
        }

        var result = await _submission.SubmitLayer4Async(analysis.Analysis, cancellationToken: cancellationToken);
        return result.Success
            ? Ok(result)
            : StatusCode(result.StatusCode > 0 ? result.StatusCode : 400, result);
    }

    [HttpPost("submit/layer3")]
    public async Task<IActionResult> SubmitLayer3(CancellationToken cancellationToken)
    {
        var result = await _layer3.FindHiddenAnswerAsync(cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Answer))
        {
            return BadRequest(new SubmissionResult(false, 400, result.Message ?? "No answer found."));
        }

        var submit = await _submission.SubmitLayer3Async(result.Answer, result.Message, cancellationToken);
        return submit.Success
            ? Ok(submit)
            : StatusCode(submit.StatusCode > 0 ? submit.StatusCode : 400, submit);
    }
}
