// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Infrastructure;

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

        // Deliberate short-circuit: do not call Next. Config-failure messages are logged (Critical)
        // above and must never reach the response body, so the body is the generic Ed-Fi 500.
        return FailureResponseWriter.WriteAsync(
            context,
            FailureResponse.ForUnknown(context.TraceIdentifier),
            context.RequestAborted
        );
    }
}
