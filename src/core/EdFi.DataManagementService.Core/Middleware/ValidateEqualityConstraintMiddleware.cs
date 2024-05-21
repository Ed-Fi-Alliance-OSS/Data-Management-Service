// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates equality constraints for a JSON document. These constraints come from implicit and explicit merges in the MetaEd model.
/// </summary>
/// <param name="_logger"></param>
/// <param name="_equalityConstraintValidator"></param>
internal class ValidateEqualityConstraintMiddleware(ILogger _logger, IEqualityConstraintValidator _equalityConstraintValidator) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateEqualityConstraintMiddleware- {TraceId}", context.FrontendRequest.TraceId);
        JsonNode? body = new JsonObject();
        if (context.FrontendRequest.Body != null)
        {
            body = JsonNode.Parse(context.FrontendRequest.Body);
        }

        string[] errors = _equalityConstraintValidator.Validate(body, context.ResourceSchema.EqualityConstraints);

        if (errors.Length == 0)
        {
            await next();
        }
        else
        {
            var failureResponse = FailureResponse.ForBadRequest(
                "The request could not be processed. See 'errors' for details.",
                null,
                errors
            );

            _logger.LogDebug(
                "'{Status}'.'{EndpointName}' - {TraceId}",
                failureResponse.status.ToString(),
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );

            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            context.FrontendResponse = new(
                StatusCode: failureResponse.status,
                Body: JsonSerializer.Serialize(failureResponse, options),
                Headers: []
            );
            return;
        }
    }
}

