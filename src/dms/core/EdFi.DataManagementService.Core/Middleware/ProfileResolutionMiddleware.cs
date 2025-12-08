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
/// Middleware that resolves which profile to apply based on request headers.
/// </summary>
internal class ProfileResolutionMiddleware : IPipelineStep
{
    private readonly ProfileResolutionService _resolutionService;
    private readonly ILogger<ProfileResolutionMiddleware> _logger;
    private readonly bool _profilesEnabled;

    public ProfileResolutionMiddleware(
        ProfileResolutionService resolutionService,
        IOptions<AppSettings> appSettings,
        ILogger<ProfileResolutionMiddleware> logger)
    {
        _resolutionService = resolutionService;
        _logger = logger;
        _profilesEnabled = appSettings.Value.EnableProfiles;
    }

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        if (!_profilesEnabled)
        {
            await next();
            return;
        }

        _logger.LogDebug("Entering ProfileResolutionMiddleware - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        // Only apply profiles to resource endpoints (not descriptors, etc.)
        if (requestInfo.ResourceSchema == No.ResourceSchema)
        {
            // Resource not yet resolved, skip
            await next();
            return;
        }

        var resourceName = requestInfo.ResourceSchema.ResourceName.Value;

        // Determine profile based on request method
        if (requestInfo.Method == RequestMethod.GET)
        {
            // Look for Accept header
            requestInfo.FrontendRequest.Headers.TryGetValue("Accept", out var acceptHeader);

            requestInfo.ProfileContentType = _resolutionService.ResolveReadProfile(acceptHeader, resourceName);
        }
        else if (requestInfo.Method == RequestMethod.POST || requestInfo.Method == RequestMethod.PUT)
        {
            // Look for Content-Type header
            requestInfo.FrontendRequest.Headers.TryGetValue("Content-Type", out var contentTypeHeader);

            requestInfo.ProfileContentType = _resolutionService.ResolveWriteProfile(contentTypeHeader, resourceName);
        }

        if (requestInfo.ProfileContentType != null)
        {
            _logger.LogDebug("Profile resolved for {Method} {ResourceName}: {MemberSelection}",
                requestInfo.Method, resourceName, requestInfo.ProfileContentType.MemberSelection);
        }

        await next();
    }
}
