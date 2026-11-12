// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates a supplied PUT body ID before replacing it with the authoritative URL ID.
/// ODS/API permits absent, null, and different valid GUID body IDs.
/// </summary>
/// <remarks>
/// Duplicate-property detection must run first so duplicate IDs receive the duplicate-property error.
/// Normalization must precede coercion so non-string IDs cannot become valid GUID strings, and
/// document validation so omitted or null IDs satisfy the normalized-document schema.
/// </remarks>
internal class NormalizePutIdMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering NormalizePutIdMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Leave non-object bodies to the existing document validation path.
        if (requestInfo.ParsedBody is JsonObject body)
        {
            if (
                body["id"] is JsonNode suppliedId
                && (
                    suppliedId is not JsonValue value
                    || !value.TryGetValue<string>(out var id)
                    || !Guid.TryParse(id, out _)
                )
            )
            {
                logger.LogDebug(
                    "Invalid PUT body ID validation: returning 400 - {TraceId}",
                    requestInfo.FrontendRequest.TraceId.Value
                );
                requestInfo.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                    new Dictionary<string, string[]>
                    {
                        ["$.id"] = ["id must be omitted, null, or a valid GUID string."],
                    },
                    requestInfo.FrontendRequest.TraceId
                );
                return;
            }

            body["id"] = requestInfo.PathComponents.DocumentUuid.Value.ToString();
        }

        await next();
    }
}
