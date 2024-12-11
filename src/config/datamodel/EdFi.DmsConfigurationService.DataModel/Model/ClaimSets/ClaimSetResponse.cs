// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetResponse
{
    public long Id { get; set; }
    public required string ClaimSetName { get; set; }
    public required bool IsSystemReserved { get; set; }
    public JsonElement? ResourceClaims { get; set; }
}

public class ClaimSetResponseReduced
{
    public long Id { get; set; }
    public required string ClaimSetName { get; set; }
    public required bool IsSystemReserved { get; set; }
}
