// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profiles.Model;

/// <summary>
/// Defines the strategy for filtering members (properties, collections) in a profile.
/// </summary>
public enum MemberSelection
{
    /// <summary>
    /// Only explicitly specified members are included; all others are excluded.
    /// </summary>
    IncludeOnly,

    /// <summary>
    /// Explicitly specified members are excluded; all others are included.
    /// </summary>
    ExcludeOnly,

    /// <summary>
    /// All members of a collection are included (used for collections within a resource).
    /// </summary>
    IncludeAll
}
