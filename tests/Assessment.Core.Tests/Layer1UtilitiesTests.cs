using Assessment.Core.Http;
using Assessment.Core.Utilities;
using Xunit;

namespace Assessment.Core.Tests;

public sealed class Layer1UtilitiesTests
{
    [Fact]
    public void ParseDatasetStats_reads_dataset_records()
    {
        const string json = """{"dataset_records":500,"batch_size":100}""";
        var stats = AssessmentHttpClient.ParseDatasetStats(json);

        Assert.NotNull(stats);
        Assert.Equal(500, stats!.TotalRecords);
        Assert.Equal(100, stats.BatchSize);
    }

    [Fact]
    public void EtagParser_extracts_sha256_from_weak_etag()
    {
        const string etag = "W/\"41154c43955ac405f9a8b4cecfb13b53e6aaf13657564fcc0c313db0d9ad7a02\"";
        var hash = EtagParser.ExtractSha256Hex(etag);

        Assert.Equal("41154c43955ac405f9a8b4cecfb13b53e6aaf13657564fcc0c313db0d9ad7a02", hash);
    }

    [Fact]
    public void DatasetBatchMerger_counts_ciphertexts_in_envelope_batches()
    {
        var batch1 = """{"count":2,"data":["abc","def"]}"""u8.ToArray();
        var batch2 = """{"count":1,"data":["ghi"]}"""u8.ToArray();
        var merged = DatasetBatchMerger.Merge([batch1, batch2]);

        Assert.Equal(3, merged.RecordCount);
        Assert.Equal(2, merged.BatchCount);
        Assert.Equal("{\"count\":2,\"data\":[\"abc\",\"def\"]}{\"count\":1,\"data\":[\"ghi\"]}", System.Text.Encoding.UTF8.GetString(merged.RawConcatenated));
    }

    [Fact]
    public void DatasetFormatParser_extracts_ciphertexts_from_concatenated_envelopes()
    {
        var dataset = """{"count":2,"data":["one","two"]}{"count":1,"data":["three"]}"""u8.ToArray();
        var ciphertexts = DatasetFormatParser.ExtractCiphertexts(dataset);

        Assert.Equal(["one", "two", "three"], ciphertexts);
    }

    [Fact]
    public void DatasetBatchMerger_merges_concatenated_json_arrays()
    {
        var batch1 = """[{"id":1},{"id":2}]"""u8.ToArray();
        var batch2 = """[{"id":3}]"""u8.ToArray();
        var merged = DatasetBatchMerger.Merge([batch1, batch2]);

        Assert.Equal(3, merged.RecordCount);
        Assert.Equal("""[{"id":1},{"id":2},{"id":3}]""", System.Text.Encoding.UTF8.GetString(merged.JsonArray));
    }
}
