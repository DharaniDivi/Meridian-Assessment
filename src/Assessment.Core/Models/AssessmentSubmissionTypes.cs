namespace Assessment.Core.Models;

public static class AssessmentSubmissionTypes
{
    public const string ContentHash = "content_hash";
    public const string DecryptedHash = "decrypted_hash";
    public const string AlgorithmAnswer = "algorithm_answer";
    public const string Analysis = "analysis";
    public const string Repo = "repo";
    public const string Transcript = "transcript";

    public static readonly IReadOnlyList<string> All =
    [
        ContentHash,
        DecryptedHash,
        AlgorithmAnswer,
        Analysis,
        Repo,
        Transcript
    ];
}
