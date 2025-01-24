// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ResourceAuthorizationMiddleware(
    ISecurityMetadataService _securityMetadataService,
    ILogger _logger
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering ResourceAuthorizationMiddleware - {TraceId}",
                context.FrontendRequest.TraceId.Value
            );
            if (context.FrontendRequest.ApiClientDetails != null)
            {
                var claimSetName = context.FrontendRequest.ApiClientDetails.ClaimSetName;
                _logger.LogInformation("Claim set name from token - {ClaimSetName}", claimSetName);
            }

            _logger.LogInformation("Retrieving claim set list");
            var claimsList = _securityMetadataService.GetClaimSets();
            var claimsJson = JsonSerializer.Serialize(claimsList);
            _logger.LogInformation(claimsJson);

            await next();
        }
        catch (ConfigurationServiceException ex)
        {
            _logger.LogError(ex, "Error while retrieving claim sets");
            context.FrontendResponse = new FrontendResponse(
                StatusCode: (int)ex.StatusCode,
                Body: ex.ErrorContent,
                Headers: []
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while retrieving claim sets");
        }
    }
}
