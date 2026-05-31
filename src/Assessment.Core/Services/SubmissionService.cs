using Assessment.Core.Http;
using Assessment.Core.Models;
using Assessment.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Assessment.Core.Configuration;

namespace Assessment.Core.Services;

public sealed class SubmissionService
{
    private readonly AssessmentHttpClient _client;
    private readonly KeyAcquisitionService _keys;
    private readonly Layer2Service _layer2;
    private readonly AssessmentOptions _options;
    private readonly ILogger<SubmissionService> _logger;

    public SubmissionService(
        AssessmentHttpClient client,
        KeyAcquisitionService keys,
        Layer2Service layer2,
        IOptions<AssessmentOptions> options,
        ILogger<SubmissionService> logger)
    {
        _client = client;
        _keys = keys;
        _layer2 = layer2;
        _options = options.Value;
        _logger = logger;
    }

    public Task<SubmissionResult> SubmitLayer1Async(string hashHex, string? notes = null, CancellationToken cancellationToken = default)
        => SubmitAsync(AssessmentSubmissionTypes.ContentHash, hashHex, notes, cancellationToken);

    public async Task<SubmissionResult> SubmitLayer2Async(string? notes = null, CancellationToken cancellationToken = default)
    {
        var hash = await _layer2.ComputeDecryptedHashAsync(cancellationToken);
        if (hash is null)
        {
            return new SubmissionResult(false, 400, "Run Layer 2 first to produce decrypted.jsonl.");
        }

        return await SubmitAsync(AssessmentSubmissionTypes.DecryptedHash, hash, notes, cancellationToken);
    }

    public Task<SubmissionResult> SubmitLayer3Async(string answer, string? notes = null, CancellationToken cancellationToken = default)
        => SubmitAsync(AssessmentSubmissionTypes.AlgorithmAnswer, answer, notes, cancellationToken);

    public Task<SubmissionResult> SubmitLayer4Async(string analysis, string? notes = null, CancellationToken cancellationToken = default)
        => SubmitAsync(AssessmentSubmissionTypes.Analysis, analysis, notes, cancellationToken);

    public Task<SubmissionResult> SubmitRepoAsync(string repoUrl, string? notes = null, CancellationToken cancellationToken = default)
        => SubmitAsync(AssessmentSubmissionTypes.Repo, repoUrl, notes, cancellationToken);

    public async Task<SubmissionResult> SubmitAsync(
        string type,
        string value,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Submitting type={Type}, value length={Length}", type, value.Length);
        var result = await _client.SubmitAsync(new SubmissionRequest(type, value, notes), cancellationToken);

        if (type.Equals(AssessmentSubmissionTypes.ContentHash, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(result.Message))
        {
            await _keys.SaveKeyFromSubmissionResponseAsync(result.Message, result.Success, cancellationToken);
        }
        else if (type.Equals(AssessmentSubmissionTypes.DecryptedHash, StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(result.Message))
        {
            await SaveSubmissionBodyAsync("submission-decrypted_hash.json", result.Message, cancellationToken);
        }

        if (!result.Success && result.ValidTypes is { Count: > 0 })
        {
            _logger.LogWarning("Submission failed. Valid types: {Types}", string.Join(", ", result.ValidTypes));
        }

        return result;
    }

    private async Task SaveSubmissionBodyAsync(string fileName, string body, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(_options.DataDirectory, fileName), body, cancellationToken);
    }
}
