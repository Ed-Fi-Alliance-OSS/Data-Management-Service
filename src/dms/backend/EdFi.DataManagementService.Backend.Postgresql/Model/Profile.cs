// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Postgresql.Model;

/// <summary>
/// A row from the Profile table representing an API Profile definition
/// </summary>
public record Profile(
    /// <summary>
    /// The unique identifier for this Profile
    /// </summary>
    long Id,
    /// <summary>
    /// The unique name of the profile
    /// </summary>
    string ProfileName,
    /// <summary>
    /// A description of what this profile represents
    /// </summary>
    string? Description,
    /// <summary>
    /// The XML definition of the profile policy
    /// </summary>
    string ProfileDefinition,
    /// <summary>
    /// The datetime this profile was created in the database
    /// </summary>
    DateTime CreatedAt,
    /// <summary>
    /// The datetime this profile was last updated in the database
    /// </summary>
    DateTime UpdatedAt
);
