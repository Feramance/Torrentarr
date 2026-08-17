using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ArrInstanceConfigJsonTests
{
    private static readonly JsonSerializerSettings PascalCase = new()
    {
        ContractResolver = new DefaultContractResolver(),
        NullValueHandling = NullValueHandling.Include
    };

    [Fact]
    public void Serialize_EmitsEntrySearch_NotSearch()
    {
        var arr = new ArrInstanceConfig
        {
            ImportMode = "Copy",
            Search = new SearchConfig
            {
                SearchMissing = false,
                DoUpgradeSearch = true
            }
        };

        var json = JsonConvert.SerializeObject(arr, PascalCase);
        var obj = JObject.Parse(json);

        obj["Search"].Should().BeNull();
        obj["EntrySearch"].Should().NotBeNull();
        obj["EntrySearch"]!["SearchMissing"]!.Value<bool>().Should().BeFalse();
        obj["EntrySearch"]!["DoUpgradeSearch"]!.Value<bool>().Should().BeTrue();
        obj["ImportMode"]!.Value<string>().Should().Be("Copy");
    }

    [Fact]
    public void Deserialize_EntrySearch_MapsToSearchProperty()
    {
        const string json = """
            {
              "ImportMode": "Copy",
              "EntrySearch": {
                "SearchMissing": false,
                "DoUpgradeSearch": true
              }
            }
            """;

        var arr = JsonConvert.DeserializeObject<ArrInstanceConfig>(json, PascalCase);

        arr.Should().NotBeNull();
        arr!.ImportMode.Should().Be("Copy");
        arr.Search.SearchMissing.Should().BeFalse();
        arr.Search.DoUpgradeSearch.Should().BeTrue();
    }
}
