using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public class DefaultAction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("authorizationStrategies")]
    public List<AuthorizationStrategy> AuthorizationStrategies { get; set; } = [];
}
