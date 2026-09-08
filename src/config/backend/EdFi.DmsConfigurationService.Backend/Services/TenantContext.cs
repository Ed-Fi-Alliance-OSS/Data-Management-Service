// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Represents the tenant context for a request. Uses a discriminated union pattern
/// to make the tenant state explicit and compiler-enforced.
/// </summary>
public abstract record TenantContext
{
    private TenantContext() { } // Prevent external inheritance

    /// <summary>
    /// Represents a context where multi-tenancy is not enabled.
    /// TenantId will be null in the database.
    /// </summary>
    public sealed record NotMultitenant : TenantContext;

    /// <summary>
    /// Represents a context where multi-tenancy is enabled and a specific tenant is identified.
    /// </summary>
    /// <param name="TenantId">The non-nullable tenant identifier.</param>
    /// <param name="TenantName">The non-nullable tenant name.</param>
    public sealed record Multitenant(long TenantId, string TenantName) : TenantContext;
}
