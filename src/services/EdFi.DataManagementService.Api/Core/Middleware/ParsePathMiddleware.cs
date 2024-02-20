// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

/// <summary>
/// Parses and validates the path from the frontend is well formed. Adds PathComponents
/// to the context if it is.
/// </summary>
public class ParsePathMiddleware(ILogger _logger) : IPipelineStep
{
    private static string Decapitalize(string str)
    {
        if (str.Length == 0) return str;
        if (str.Length == 1) return str.ToLower();
        return str[0..1].ToLower() + str[1..];
    }

    private static PathComponents? PathComponentsFrom(string path)
    {
        // Matches all of the following sample expressions:
        // /ed-fi/Sections
        // /ed-fi/Sections/
        // /ed-fi/Sections/idValue
        Regex pathExpression = new(@"\/(?<projectNamespace>[^/]+)\/(?<endpointName>[^/]+)(\/|$)((?<documentUuid>[^/]*$))?");

        Match? match = pathExpression.Match(path);

        if (match == null) return null;

        string? documentUuidValue = match.Groups["documentUuid"].Value;
        if (documentUuidValue == "") documentUuidValue = null;

        return new(
          ProjectNamespace: new(match.Groups["projectNamespace"].Value.ToLower()),
          EndpointName: new(Decapitalize(match.Groups["endpointName"].Value)),
          DocumentUuid: documentUuidValue == null ? null : new(documentUuidValue)
        );
    }

    /// <summary>
    /// Check that this is a well formed UUID v4
    /// </summary>
    private static bool IsDocumentUuidWellFormed(DocumentUuid documentUuid)
    {
        // Regex for a UUID v4 string
        Regex uuid4Regex = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[4][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");

        return uuid4Regex.IsMatch(documentUuid.Value.ToLower());
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogInformation("ParsePathMiddleware");

        PathComponents? pathComponents = PathComponentsFrom(context.FrontendRequest.Path);

        if (pathComponents == null)
        {
            context.FrontendResponse = new(StatusCode: 404, Body: "");
            return;
        }

        context.PathComponents = pathComponents;

        if (pathComponents.DocumentUuid != null && !IsDocumentUuidWellFormed(pathComponents.DocumentUuid.Value))
        {
            context.FrontendResponse = new(StatusCode: 404, Body: "");
            return;
        }

        await next();
    }
}
