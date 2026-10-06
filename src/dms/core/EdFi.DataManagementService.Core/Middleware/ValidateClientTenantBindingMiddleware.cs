// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Confirms the authenticated client's application is bound to the tenant the identity request was
/// routed to. Resolves <see cref="IApplicationContextProvider" /> from the request scope rather than
/// taking a constructor dependency, matching <see cref="ApplicationContextRequirementMiddleware" />.
/// Unlike that middleware, this step runs on every identity request regardless of method or
/// authorization strategy, stores nothing from the resolved context (no <see cref="RequestInfo.ApplicationContext" />
/// assignment), and never inspects <c>DataStoreIds</c> - identity operations are datastore-independent.
/// <see cref="ApplicationContextResult.NotFound" /> and <see cref="ApplicationContextResult.Unavailable" />
/// reuse <see cref="ApplicationContextFailureResponseFactory" /> for the same 401/503 shapes.
/// </summary>
internal sealed class ValidateClientTenantBindingMiddleware(
    ILogger<ValidateClientTenantBindingMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        var provider = requestInfo.ScopedServiceProvider.GetRequiredService<IApplicationContextProvider>();

        ApplicationContextResult result = await provider.GetApplicationByClientIdAsync(
            requestInfo.ClientAuthorizations.ClientId,
            requestInfo.FrontendRequest.Tenant,
            requestInfo.RequestCancellationToken
        );

        switch (result)
        {
            case ApplicationContextResult.Success:
                await next();
                return;
            case ApplicationContextResult.NotFound:
                logger.LogWarning(
                    "Client's application is not bound to the requested tenant - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = ApplicationContextFailureResponseFactory.Create(
                    result,
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            case ApplicationContextResult.Unavailable:
                logger.LogWarning(
                    "Client-to-tenant binding was unavailable - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = ApplicationContextFailureResponseFactory.Create(
                    result,
                    requestInfo.FrontendRequest.TraceId
                );
                return;
        }
    }
}
