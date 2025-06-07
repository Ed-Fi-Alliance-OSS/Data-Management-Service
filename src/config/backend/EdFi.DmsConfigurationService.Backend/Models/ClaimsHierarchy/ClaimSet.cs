using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public class ClaimSet
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("actions")]
    public List<ClaimSetAction> Actions { get; set; } = [];
}
