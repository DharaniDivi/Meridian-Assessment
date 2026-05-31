using System.Text;
using Assessment.Core.Utilities;
using Xunit;

namespace Assessment.Core.Tests;

public sealed class DatasetIntegrityHasherTests
{
    [Fact]
    public void Compute_builds_data_only_envelope_from_raw_literals()
    {
        var batch1 = """{"count":2,"data":["YWJj","ZGVm"]}"""u8.ToArray();
        var batch2 = """{"count":1,"data":["Z2hp"]}"""u8.ToArray();

        var result = DatasetIntegrityHasher.Compute([batch1, batch2]);

        Assert.Equal(3, result.CiphertextCount);
        Assert.Equal("rawConcat", result.PrimaryFormat);
        Assert.True(result.All.ContainsKey("rawConcat"));
        Assert.True(result.All.ContainsKey("dataOnlyEnvelope"));
        Assert.Equal("{\"data\":[\"YWJj\",\"ZGVm\",\"Z2hp\"]}", Encoding.UTF8.GetString(DatasetIntegrityHasher.BuildCanonicalDatasetBytes([batch1, batch2])));
    }

    [Fact]
    public void Compute_preserves_literal_plus_from_api_bytes()
    {
        var batch = """{"count":1,"data":["abc+def/ghi=="]}"""u8.ToArray();

        var canonical = Encoding.UTF8.GetString(DatasetIntegrityHasher.BuildCanonicalDatasetBytes([batch]));

        Assert.Contains("+", canonical);
        Assert.DoesNotContain("\\u002B", canonical);
    }

    [Fact]
    public void Compute_real_dataset_when_cached()
    {
        var datasetPath = @"C:\AA-PROJECT\src\Assessment.Api\data\dataset.bin";
        if (!File.Exists(datasetPath))
        {
            return;
        }

        var bytes = File.ReadAllBytes(datasetPath);
        var batches = SplitBatchBytes(bytes);
        var result = DatasetIntegrityHasher.Compute(batches);

        Assert.True(result.CiphertextCount >= 100, $"Expected many ciphertexts, got {result.CiphertextCount}");
        Assert.True(result.All.Count >= 5);
    }

    private static List<byte[]> SplitBatchBytes(byte[] datasetBytes)
    {
        var batches = new List<byte[]>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < datasetBytes.Length; i++)
        {
            if (datasetBytes[i] == (byte)'{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
            }
            else if (datasetBytes[i] == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    batches.Add(datasetBytes[start..(i + 1)]);
                }
            }
        }

        return batches;
    }
}
