// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security.Model;

/// <summary>
/// The claims used for resource authorization
/// </summary>
public record ResourceClaim(
    /// <summary>
    /// Resource claim name
    /// </summary>
    string? Name,
    /// <summary>
    /// Action that can be performed on the resource
    /// </summary>
    string? Action,
    /// <summary>
    /// Authorization strategy for the resource
    /// </summary>
    AuthorizationStrategy[] AuthorizationStrategies
);
