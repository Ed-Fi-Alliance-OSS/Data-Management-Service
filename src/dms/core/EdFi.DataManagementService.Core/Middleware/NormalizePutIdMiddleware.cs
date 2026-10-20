// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates a supplied PUT body ID before replacing it with the authoritative URL ID.
/// ODS/API permits absent, null, and different valid GUID body IDs.
/// </summary>
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
                requestInfo.FrontendResponse = new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForDataValidation(
                        FailureResponse.ValidationErrorsArmDetail,
                        requestInfo.FrontendRequest.TraceId,
                        new Dictionary<string, string[]> { ["$.id"] = ["id must be a valid GUID string."] },
                        []
                    ),
                    Headers: []
                );
                return;
            }

            body["id"] = requestInfo.PathComponents.DocumentUuid.Value.ToString();
        }

        await next();
    }
}
