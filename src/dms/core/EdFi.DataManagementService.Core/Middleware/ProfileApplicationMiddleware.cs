// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that applies profile filtering to request or response bodies.
/// </summary>
internal class ProfileApplicationMiddleware : IPipelineStep
{
    private readonly ProfileApplicationService _applicationService;
    private readonly ILogger<ProfileApplicationMiddleware> _logger;
    private readonly bool _profilesEnabled;

    public ProfileApplicationMiddleware(
        ProfileApplicationService applicationService,
        IOptions<AppSettings> appSettings,
        ILogger<ProfileApplicationMiddleware> logger)
    {
        _applicationService = applicationService;
        _logger = logger;
        _profilesEnabled = appSettings.Value.EnableProfiles;
    }

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (!_profilesEnabled || requestInfo.ProfileContentType == null)
        {
            await next();
            return;
        }

        _logger.LogDebug("Entering ProfileApplicationMiddleware - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        // For POST/PUT, filter the request body before continuing
        if (requestInfo.Method == RequestMethod.POST || requestInfo.Method == RequestMethod.PUT)
        {
            if (requestInfo.ParsedBody != null && requestInfo.ProfileContentType != null)
            {
                _logger.LogDebug("Applying write profile filter to request body");
                var filteredBody = _applicationService.ApplyFilter(
                    requestInfo.ParsedBody,
                    requestInfo.ProfileContentType
                );
                // ApplyFilter should preserve non-null input as non-null output
                if (filteredBody != null)
                {
                    requestInfo.ParsedBody = filteredBody;
                }
            }

            await next();
            return;
        }

        // For GET, filter the response body after processing
        if (requestInfo.Method == RequestMethod.GET)
        {
            await next();

            // Filter response if we have one
            if (requestInfo.FrontendResponse?.Body != null)
            {
                _logger.LogDebug("Applying read profile filter to response body");
                var filteredBody = _applicationService.ApplyFilter(
                    requestInfo.FrontendResponse.Body,
                    requestInfo.ProfileContentType
                );

                // Update the response with filtered body
                requestInfo.FrontendResponse = new FrontendResponse(
                    requestInfo.FrontendResponse.StatusCode,
                    filteredBody,
                    requestInfo.FrontendResponse.Headers,
                    requestInfo.FrontendResponse.ContentType
                );
            }

            return;
        }

        // Other methods, just continue
        await next();
    }
}
