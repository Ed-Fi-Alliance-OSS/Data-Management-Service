// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class AuthorizationStrategy
{
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public required string AuthorizationStrategyName { get; set; }

    public string? DisplayName { get; set; }
}
