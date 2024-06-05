// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class ValidateMatchingDocumentUuidsMiddleware(ILogger _logger, IMatchingDocumentUuidsValidator _validator) : IPipelineStep
    {
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug("Entering ValidateMatchingDocumentUuidsMiddleware- {TraceId}", context.FrontendRequest.TraceId);

            var isValid = _validator.Validate(context);

            if (isValid)
            {
                await next();
            }
            else
            {
                FailureResponse failureResponse = FailureResponse.ForBadRequest(
                    "The request could not be processed. See 'errors' for details.",
                    null,
                    ["Request body id must match the id in the url."]
                );

                _logger.LogDebug(
                    "'{Status}'.'{EndpointName}' - {TraceId}",
                    failureResponse.status.ToString(),
                    context.PathComponents.EndpointName,
                    context.FrontendRequest.TraceId
                );

                var options = new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true
                };

                context.FrontendResponse = new FrontendResponse(
                    StatusCode: failureResponse.status,
                    Body: JsonSerializer.Serialize(failureResponse, options),
                    Headers: []
                );
            }
        }
    }
}
