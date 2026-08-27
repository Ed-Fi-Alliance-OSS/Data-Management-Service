// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Requires application context for POST requests and resource actions that use ownership-based
/// authorization after their effective strategies have been selected.
/// </summary>
internal sealed class ApplicationContextRequirementMiddleware(
    ILogger<ApplicationContextRequirementMiddleware> logger
) : IPipelineStep
{
    private const string OwnershipBasedStrategy = "OwnershipBased";
    private const string ApplicationContextUnavailableError =
        "Unable to resolve application context for the authenticated client.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        bool contextRequired =
            requestInfo.Method is RequestMethod.POST
            || (
                requestInfo.Method is RequestMethod.GET or RequestMethod.PUT or RequestMethod.DELETE
                && requestInfo.ResourceActionAuthStrategies.Contains(
                    OwnershipBasedStrategy,
                    StringComparer.OrdinalIgnoreCase
                )
            );

        if (!contextRequired)
        {
            if (requestInfo.DeferredProfileContextFailureResponse is not null)
            {
                requestInfo.FrontendResponse = requestInfo.DeferredProfileContextFailureResponse;
                return;
            }

            await next();
            return;
        }

        var provider = requestInfo.ScopedServiceProvider.GetRequiredService<IApplicationContextProvider>();
        ApplicationContextResult result = await provider.GetApplicationByClientIdAsync(
            requestInfo.ClientAuthorizations.ClientId,
            requestInfo.FrontendRequest.Tenant
        );

        switch (result)
        {
            case ApplicationContextResult.Success success:
                requestInfo.ApplicationContext = success.ApplicationContext;
                await next();
                return;
            case ApplicationContextResult.NotFound:
                logger.LogWarning(
                    "Required application context was not found - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 401,
                    Body: FailureResponse.ForAuthenticationFailure(
                        requestInfo.FrontendRequest.TraceId,
                        [ApplicationContextUnavailableError]
                    ),
                    Headers: new Dictionary<string, string>
                    {
                        ["WWW-Authenticate"] = "Bearer error=\"invalid_token\"",
                    },
                    ContentType: "application/problem+json"
                );
                return;
            case ApplicationContextResult.Unavailable:
                logger.LogWarning(
                    "Required application context was unavailable - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 503,
                    Body: FailureResponse.ForServiceUnavailable(requestInfo.FrontendRequest.TraceId),
                    Headers: [],
                    ContentType: "application/problem+json"
                );
                return;
        }
    }
}
