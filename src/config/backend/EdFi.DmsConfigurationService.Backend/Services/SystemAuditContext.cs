// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Provides audit context for system operations that occur outside of an HTTP request context.
/// Used during application startup, background jobs, or system-initiated operations.
/// Always returns "system" as the current user.
/// </summary>
public class SystemAuditContext : IAuditContext
{
    /// <summary>
    /// Gets the identifier for system operations.
    /// Always returns "system" since there's no authenticated user context.
    /// </summary>
    public string GetCurrentUser() => "system";

    /// <summary>
    /// Gets the current UTC timestamp for audit tracking.
    /// </summary>
    public DateTime GetCurrentTimestamp() => DateTime.UtcNow;
}
