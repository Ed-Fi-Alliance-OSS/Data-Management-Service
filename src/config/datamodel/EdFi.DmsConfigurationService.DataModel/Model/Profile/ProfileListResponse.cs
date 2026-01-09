// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Profile;

/// <summary>
/// Represents a summary of a profile for list operations, containing only Id and Name.
/// </summary>
public class ProfileListResponse
{
    /// <summary>
    /// Gets or sets the unique identifier for the profile.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the name of the profile.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
