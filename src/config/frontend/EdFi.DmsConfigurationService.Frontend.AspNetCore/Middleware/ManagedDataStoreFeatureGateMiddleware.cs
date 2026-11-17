// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;

/// <summary>
/// Returns the disabled-feature 400 for a route marked with <see cref="ManagedDataStoreRouteMetadata"/> while
/// management is disabled. Registered after authorization, so 401/403 keep precedence, and before
/// <see cref="JsonCharsetValidationMiddleware"/>, so no request encoding or body can change the disabled
/// response. It resolves no managed repository or secret provider.
/// </summary>
public class ManagedDataStoreFeatureGateMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context, JobClaimPolicy policy)
    {
        if (
            policy.ExcludesManagedDataStores
            && context.GetEndpoint()?.Metadata.GetMetadata<ManagedDataStoreRouteMetadata>() is not null
        )
        {
            await DataStoreManageModule.Disabled(context).ExecuteAsync(context);
            return;
        }

        await _next(context);
    }
}
