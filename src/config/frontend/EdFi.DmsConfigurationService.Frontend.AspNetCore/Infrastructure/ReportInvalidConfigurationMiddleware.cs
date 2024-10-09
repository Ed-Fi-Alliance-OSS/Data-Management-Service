// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public class ReportInvalidConfigurationMiddleware(RequestDelegate next, List<string> errors)
{
    public RequestDelegate Next { get; } = next;

    public Task Invoke(HttpContext context, ILogger<ReportInvalidConfigurationMiddleware> logger)
    {
        foreach (var error in errors)
        {
            logger.LogCritical(error);
        }

        context.Response.StatusCode = 500;
        return Task.CompletedTask;
    }
}
