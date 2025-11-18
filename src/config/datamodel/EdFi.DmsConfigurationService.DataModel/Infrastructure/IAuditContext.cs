// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

/// <summary>
/// Provides access to audit context information for tracking record creation and modification.
/// </summary>
public interface IAuditContext
{
    /// <summary>
    /// Gets the identifier of the current authenticated user or client.
    /// Returns "system" for system operations or when no user context is available.
    /// </summary>
    /// <returns>The user identifier (username, client_id, or "system")</returns>
    string GetCurrentUser();

    /// <summary>
    /// Gets the current UTC timestamp for audit tracking.
    /// </summary>
    /// <returns>The current UTC date and time</returns>
    DateTime GetCurrentTimestamp();
}
