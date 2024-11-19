// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class ValidateDescriptorMiddleware(ILogger _logger, IDescriptorValidator _descriptorValidator) : IPipelineStep
    {
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering ValidateDescriptorMiddleware - {TraceId}",
                context.FrontendRequest.TraceId
            );
            if (context.ResourceSchema.IsDescriptor)
            {
                bool isValid = _descriptorValidator.Validate(context);
                if (!isValid)
                {
                    var failureResponse = FailureResponse.ForDataValidation(
                        "Data validation failed. See 'validationErrors' for details.",
                        context.FrontendRequest.TraceId,
                        new Dictionary<string, string[]>
                        {
                            {"namespace", [$"Namespace must be a valid uri in the format uri://{{namespace}}/{context.ResourceSchema.ResourceName.Value}"]}
                        },
                        []
                    );

                    _logger.LogDebug(
                        "'{Status}'.'{EndpointName}' - {TraceId}",
                        "400",
                        context.PathComponents.EndpointName,
                        context.FrontendRequest.TraceId
                    );

                    context.FrontendResponse = new FrontendResponse(
                        StatusCode: 400,
                        Body: failureResponse,
                        Headers: []
                    );

                    return;
                }
            }

            await next();
        }
    }
}
