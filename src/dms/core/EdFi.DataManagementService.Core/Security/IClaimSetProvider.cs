// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Provides claim sets with resource claims and authorization strategies.
/// </summary>
public interface IClaimSetProvider
{
    /// <summary>
    /// Retrieves all claim sets with their associated resource claims and authorization strategies.
    /// </summary>
    Task<IList<ClaimSet>> GetAllClaimSets();
}
