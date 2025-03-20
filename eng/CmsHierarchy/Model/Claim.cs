// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace CmsHierarchy.Model;

public class Claim
{
    [JsonIgnore]
    [JsonPropertyName("claimId")]
    public int ClaimId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("defaultAuthorization")]
    public DefaultAuthorization? DefaultAuthorization { get; set; }

    [JsonPropertyName("claimSets")]
    public List<ClaimSet>? ClaimSets { get; set; }

    [JsonPropertyName("claims")]
    public List<Claim>? Claims { get; set; }
}

public class DefaultAuthorization
{
    [JsonPropertyName("actions")]
    public List<Action>? Actions { get; set; }
}

public class Action
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("authorizationStrategies")]
    public List<AuthorizationStrategy>? AuthorizationStrategies { get; set; }
}

public class ClaimSetAction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("authorizationStrategyOverrides")]
    public List<AuthorizationStrategy>? AuthorizationStrategyOverrides { get; set; }
}

public class AuthorizationStrategy
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
