using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;

public class Claim
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("defaultAuthorization")]
    public DefaultAuthorization? DefaultAuthorization { get; set; }

    [JsonPropertyName("claimSets")]
    public List<ClaimSet> ClaimSets { get; set; } = [];

    private List<Claim> _claims = new();

    [JsonPropertyName("claims")]
    public List<Claim> Claims
    {
        get => _claims;
        set
        {
            _claims = value;

            // Provide navigability up the claims hierarchy
            foreach (Claim claim in _claims)
            {
                claim.Parent = this;
            }
        }
    }

    [JsonIgnore]
    public Claim? Parent { get; set; }
}
