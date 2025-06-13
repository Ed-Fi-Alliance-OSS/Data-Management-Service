using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public class DefaultAuthorization
{
    [JsonPropertyName("actions")]
    public List<DefaultAction> Actions { get; set; } = [];
}
