// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;

/// <summary>
/// Service for validating claims table state
/// </summary>
public interface IClaimsTableValidator
{
    /// <summary>
    /// Checks if both ClaimSet and ClaimsHierarchy tables are empty
    /// </summary>
    /// <returns>True if both tables are empty, false otherwise</returns>
    Task<bool> AreClaimsTablesEmptyAsync();
}
