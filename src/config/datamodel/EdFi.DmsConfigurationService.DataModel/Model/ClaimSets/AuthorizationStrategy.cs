// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class AuthorizationStrategy
{
    [JsonPropertyName("id")]
    public int AuthStrategyId { get; set; }
    [JsonPropertyName("name")]
    public string? AuthStrategyName { get; set; }
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}
