// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Parses and validates the path from the frontend is well formed. Adds PathComponents
/// to the context if it is.
/// </summary>
internal partial class ParsePathMiddleware(ILogger _logger) : IPipelineStep
{
    // Matches all of the following sample expressions:
    // /ed-fi/sections
    // /ed-fi/sections/
    // /ed-fi/sections/idValue
    [GeneratedRegex(@"\/(?<projectNamespace>[^/]+)\/(?<endpointName>[^/]+)(\/|$)((?<documentUuid>[^/]*$))?")]
    private static partial Regex PathExpressionRegex();

    // Regex for a UUID v4 string
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[4][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex Uuid4Regex();

    /// <summary>
    /// Uses a regex to split the path into PathComponents, or return null if the path is invalid
    /// </summary>
    private static PathComponents? PathComponentsFrom(string path)
    {
        Match match = PathExpressionRegex().Match(path);

        if (!match.Success)
        {
            return null;
        }

        string documentUuidValue = match.Groups["documentUuid"].Value;
        DocumentUuid documentUuid = documentUuidValue == "" ? No.DocumentUuid : new(documentUuidValue);

        return new(
            ProjectNamespace: new(match.Groups["projectNamespace"].Value.ToLower()),
            EndpointName: new(match.Groups["endpointName"].Value),
            DocumentUuid: documentUuid
        );
    }

    /// <summary>
    /// Check that this is a well formed UUID v4
    /// </summary>
    private static bool IsDocumentUuidWellFormed(DocumentUuid documentUuid)
    {
        return Uuid4Regex().IsMatch(documentUuid.Value.ToLower());
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ParsePathMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        PathComponents? pathComponents = PathComponentsFrom(context.FrontendRequest.Path);

        if (pathComponents == null)
        {
            _logger.LogDebug(
                "ParsePathMiddleware: Not a valid path - {TraceId}",
                context.FrontendRequest.TraceId
            );
            context.FrontendResponse = new(StatusCode: 404, Body: "");
            return;
        }

        context.PathComponents = pathComponents;

        if (
            pathComponents.DocumentUuid != No.DocumentUuid
            && !IsDocumentUuidWellFormed(pathComponents.DocumentUuid)
        )
        {
            _logger.LogDebug(
                "ParsePathMiddleware: Not a valid document UUID - {TraceId}",
                context.FrontendRequest.TraceId
            );
            context.FrontendResponse = new(StatusCode: 404, Body: "");
            return;
        }

        await next();
    }
}
