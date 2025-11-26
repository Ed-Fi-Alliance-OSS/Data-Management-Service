// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Holds the tenant context for the current request scope.
/// This is a mutable holder that allows middleware to set the resolved tenant context.
/// </summary>
public interface ITenantContextProvider
{
    /// <summary>
    /// Gets or sets the current tenant context.
    /// Defaults to <see cref="TenantContext.NotMultitenant"/> until set by middleware.
    /// </summary>
    TenantContext Context { get; set; }
}

/// <summary>
/// Default implementation of <see cref="ITenantContextProvider"/>.
/// Registered as scoped to ensure one instance per HTTP request.
/// </summary>
public class TenantContextProvider : ITenantContextProvider
{
    public TenantContext Context { get; set; } = new TenantContext.NotMultitenant();
}
