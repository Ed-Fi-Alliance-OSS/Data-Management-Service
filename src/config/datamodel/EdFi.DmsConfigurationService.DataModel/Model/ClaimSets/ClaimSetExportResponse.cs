// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetExportResponse
{
    public long Id { get; set; }
    public required string Name { get; set; }
    [JsonPropertyName("_isSystemReserved")]
    public required bool IsSystemReserved { get; set; }
    [JsonPropertyName("_applications")]
    public JsonElement? Applications { get; set; }
    public required JsonElement ResourceClaims { get; set; }
}

