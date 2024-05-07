// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Infrastructure;
using EdFi.DataManagementService.Backend.Deploy;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServices();

var app = builder.Build();

app.UseMiddleware<LoggingMiddleware>();
app.UseValidationErrorsHandlingMiddleware();
if (!InjectInvalidConfigurationMiddleware(app))
{
    InitializeDatabase(app);
}

app.UseRouting();
app.UseRateLimiter();
app.MapRouteEndpoints();

app.Run();
return;

bool InjectInvalidConfigurationMiddleware(WebApplication app)
{
    try
    {
        // Accessing IOptions<T> forces validation
        _ = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value;
    }
    catch (OptionsValidationException ex)
    {
        app.UseMiddleware<InvalidConfigurationMiddleware>(ex.Failures);
        return true;
    }
    return false;
}

void InitializeDatabase(WebApplication app)
{
    if (app.Services.GetRequiredService<IOptions<AppSettings>>().Value.DeployDatabaseOnStartup)
    {
        var result = app.Services.GetRequiredService<IDatabaseDeploy>().DeployDatabase(app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value.DatabaseConnection);
        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            app.Logger.LogCritical("Database Deploy Failure");
            throw failure.Error;
        }
    }
}


public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
