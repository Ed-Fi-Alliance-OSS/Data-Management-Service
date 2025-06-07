using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public class AuthorizationStrategy
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}
