using Assessment.Core.Services;
using Xunit;

namespace Assessment.Core.Tests;

public class Layer2ServiceTests
{
    [Fact]
    public void ParseRecords_ParsesJsonArray()
    {
        var json = """[{"id":1,"token":"Alpha"},{"id":2,"token":"Beta"}]""";
        var records = Layer2Service.ParseRecords(json);

        Assert.Equal(2, records.Count);
        Assert.Equal("Alpha", records[0].Fields["token"]);
    }

    [Fact]
    public void ParseRecords_ParsesJsonLines()
    {
        var json = """
            {"id":1,"token":"Alpha"}
            {"id":2,"token":"Beta"}
            """;

        var records = Layer2Service.ParseRecords(json);
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public void DecryptPayload_ReturnsPlainJsonWhenAlreadyPlaintext()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"ok":true}""");
        var result = Layer2Service.DecryptPayload(bytes, "any-key");
        Assert.Contains("ok", result);
    }
}
