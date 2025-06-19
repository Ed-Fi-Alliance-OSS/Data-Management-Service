using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public class ClaimSetAction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("authorizationStrategyOverrides")]
    public List<AuthorizationStrategy> AuthorizationStrategyOverrides { get; set; } = [];
}
