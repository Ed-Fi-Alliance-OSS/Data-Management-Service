// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// ReSharper disable ClassNeverInstantiated.Global

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IClaimsHierarchyRepository
{
    Task<ClaimsHierarchyResult> GetClaimsHierarchy();
}

public abstract record ClaimsHierarchyResult
{
    /// <summary>
    /// Successfully loaded and deserialized the claim set hierarchy.
    /// </summary>
    /// <param name="Claims"></param>
    public record Success(Claim[] Claims) : ClaimsHierarchyResult;

    /// <summary>
    /// The claims hierarchy was not found.
    /// </summary>
    public record FailureHierarchyNotFound() : ClaimsHierarchyResult;

    /// <summary>
    /// Unexpected exception thrown and caught.
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimsHierarchyResult;
}

public class Claim
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("defaultAuthorization")]
    public DefaultAuthorization? DefaultAuthorization { get; set; }

    [JsonPropertyName("claimSets")]
    public List<ClaimSet> ClaimSets { get; set; } = new();

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

public class DefaultAuthorization
{
    [JsonPropertyName("action")]
    public List<DefaultAction> Actions { get; set; } = new();
}

public class DefaultAction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("authorizationStrategies")]
    public List<AuthorizationStrategy> AuthorizationStrategies { get; set; } = new();
}

public class ClaimSetAction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("authorizationStrategyOverrides")]
    public List<AuthorizationStrategy> AuthorizationStrategyOverrides { get; set; } = new();
}

public class AuthorizationStrategy
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}

public class ClaimSet
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("actions")]
    public List<ClaimSetAction> Actions { get; set; } = new();
}
