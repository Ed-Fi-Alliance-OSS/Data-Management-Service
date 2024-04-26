// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Api.Infrastructure;

public class ConfigurationValidationMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger, IOptions<AppSettings> appSettings, IOptions<ConnectionStrings> connectionStrings)
    {
        var isValid = true;

        if (appSettings.Value.AuthenticationService == null)
        {
            logger.LogCritical("Missing required AppSettings value: AuthenticationService");
            isValid = false;
        }

        if (connectionStrings.Value.DatabaseConnection == null)
        {
            logger.LogCritical("Missing required ConnectionStrings value: DatabaseConnection");
            isValid = false;
        }

        if (isValid)
            await next(context);
        else
            context.Response.StatusCode = 500;
    }
}
