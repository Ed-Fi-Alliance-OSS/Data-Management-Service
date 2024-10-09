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

internal record PathInfo(string ProjectNamespace, string EndpointName, string? DocumentUuid);

/// <summary>
/// Parses and validates the path from the frontend is well-formed. Adds PathComponents
/// to the context if it is.
/// </summary>
internal class ParsePathMiddleware(ILogger _logger) : IPipelineStep
{
    /// <summary>
    /// Uses a regex to split the path into PathComponents, or return null if the path is invalid
    /// </summary>
    private static PathInfo? PathInfoFrom(string path)
    {
        Match match = UtilityService.PathExpressionRegex().Match(path);

        if (!match.Success)
        {
            return null;
        }

        string documentUuidValue = match.Groups["documentUuid"].Value;
        string? documentUuid = documentUuidValue == "" ? null : documentUuidValue;

        return new(
            ProjectNamespace: new(match.Groups["projectNamespace"].Value.ToLower()),
            EndpointName: new(match.Groups["endpointName"].Value),
            DocumentUuid: documentUuid
        );
    }

    /// <summary>
    /// Check that this is a well-formed UUID v4 string
    /// </summary>
    private static bool IsDocumentUuidWellFormed(string documentUuidString)
    {
        return UtilityService.Uuid4Regex().IsMatch(documentUuidString.ToLower());
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ParsePathMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        PathInfo? pathInfo = PathInfoFrom(context.FrontendRequest.Path);

        if (pathInfo == null)
        {
            _logger.LogDebug(
                "ParsePathMiddleware: Not a valid path - {TraceId}",
                context.FrontendRequest.TraceId
            );
            context.FrontendResponse = new FrontendResponse(StatusCode: 404, Body: "", Headers: []);
            return;
        }

        if (pathInfo.DocumentUuid != null && !IsDocumentUuidWellFormed(pathInfo.DocumentUuid))
        {
            _logger.LogDebug(
                "ParsePathMiddleware: Not a valid document UUID - {TraceId}",
                context.FrontendRequest.TraceId
            );

            context.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForDataValidation(
                    detail: "Data validation failed. See 'validationErrors' for details.",
                    traceId: context.FrontendRequest.TraceId,
                    validationErrors: new Dictionary<string, string[]>
                    {
                        { "$.id", new[] { $"The value '{pathInfo.DocumentUuid}' is not valid." } }
                    },
                    errors: []
                ),
                Headers: []
            );
            return;
        }

        DocumentUuid documentUuid =
            pathInfo.DocumentUuid == null ? No.DocumentUuid : new(new(pathInfo.DocumentUuid));

        context.PathComponents = new(
            ProjectNamespace: new(pathInfo.ProjectNamespace),
            EndpointName: new(pathInfo.EndpointName),
            DocumentUuid: documentUuid
        );

        await next();
    }
}
