// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Security;

public class ConfigurationServiceResponseHandler(ILogger<ConfigurationServiceResponseHandler> logger)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = response.StatusCode;
            var exceptionDetails =
                $"Error response from {request.RequestUri}. Error message: {errorContent}. StatusCode: {statusCode}";

            logger.LogError(
                "Error response from {RequestUri}. Error message: {Content}. StatusCode: {StatusCode}",
                request.RequestUri,
                errorContent,
                statusCode
            );

            throw new HttpRequestException(exceptionDetails);
        }

        return response;
    }
}
