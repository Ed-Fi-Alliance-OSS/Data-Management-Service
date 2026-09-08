// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal sealed class ParseTrackedChangePathMiddleware(ILogger _logger) : IPipelineStep
{
    private static readonly Regex PathRegex = new(
        "^/(?<projectNamespace>[^/]+)/(?<endpointName>[^/]+)/(?<operation>[^/]+)/?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled
    );

    private static ChangeQueryEndpointOperation? OperationFrom(string operation) =>
        operation switch
        {
            _ when string.Equals(operation, "deletes", StringComparison.OrdinalIgnoreCase) =>
                ChangeQueryEndpointOperation.Deletes,
            _ when string.Equals(operation, "keyChanges", StringComparison.OrdinalIgnoreCase) =>
                ChangeQueryEndpointOperation.KeyChanges,
            _ => null,
        };

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ParseTrackedChangePathMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        Match match = PathRegex.Match(requestInfo.FrontendRequest.Path);
        ChangeQueryEndpointOperation? operation = match.Success
            ? OperationFrom(match.Groups["operation"].Value)
            : null;

        if (!match.Success || operation is null)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: FailureResponse.ForNotFound(
                    "The specified data could not be found.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        requestInfo.PathComponents = new(
            ProjectEndpointName: new(match.Groups["projectNamespace"].Value.ToLowerInvariant()),
            EndpointName: new(match.Groups["endpointName"].Value),
            DocumentUuid: No.DocumentUuid,
            HasDocumentUuidSegment: false
        );
        requestInfo.ChangeQueryOperation = operation.Value;

        await next();
    }
}
