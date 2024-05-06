// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Api.Infrastructure;

public class InvalidApiSchemaMiddleware(
    RequestDelegate next,
    Dictionary<string, List<string>> validationErrors
)
{
    public RequestDelegate Next { get; } = next;

    public Task Invoke(HttpContext context, ILogger<InvalidApiSchemaMiddleware> logger)
    {
        if (validationErrors.Any())
        {
            foreach (var validationError in validationErrors)
            {
                logger.LogCritical($"Path:{validationError.Key}, Errors: {validationError.Value}");
            }
        }

        context.Response.StatusCode = 500;
        return Task.CompletedTask;
    }
}
