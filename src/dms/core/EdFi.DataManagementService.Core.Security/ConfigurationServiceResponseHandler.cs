// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;

namespace EdFi.DataManagementService.Core.Security;

public class ConfigurationServiceResponseHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var statusCode = response.StatusCode;

                switch (statusCode)
                {
                    case HttpStatusCode.BadRequest:
                        throw new ConfigurationServiceException(
                            "Bad Request",
                            statusCode,
                            errorContent,
                            request.RequestUri?.AbsolutePath
                        );
                    case HttpStatusCode.Unauthorized:
                        throw new ConfigurationServiceException(
                            "Unauthorized",
                            statusCode,
                            errorContent,
                            request.RequestUri?.AbsolutePath
                        );
                    case HttpStatusCode.Forbidden:
                        throw new ConfigurationServiceException(
                            "Forbidden",
                            statusCode,
                            errorContent,
                            request.RequestUri?.AbsolutePath
                        );
                    case HttpStatusCode.NotFound:
                        throw new ConfigurationServiceException(
                            "Not Found",
                            statusCode,
                            errorContent,
                            request.RequestUri?.AbsolutePath
                        );
                    case HttpStatusCode.InternalServerError:
                        throw new ConfigurationServiceException(
                            "Internal Server Error",
                            statusCode,
                            errorContent,
                            request.RequestUri?.AbsolutePath
                        );
                    default:
                        throw new ConfigurationServiceException(
                            "Unexpected Error",
                            statusCode,
                            errorContent,
                            request.RequestUri?.AbsolutePath
                        );
                }
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new ConfigurationServiceException(
                "A network error occurred.",
                HttpStatusCode.ServiceUnavailable,
                ex.Message,
                string.Empty
            );
        }
    }
}

public class ConfigurationServiceException(
    string message,
    HttpStatusCode statusCode,
    string errorContent,
    string? url
) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ErrorContent { get; } = errorContent;
    public string? Url { get; } = url;
}
