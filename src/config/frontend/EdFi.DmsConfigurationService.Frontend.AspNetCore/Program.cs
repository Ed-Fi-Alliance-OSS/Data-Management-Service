// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServices();
var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();

if (!ReportInvalidConfiguration(app))
{
    InitializeDatabase(app);
}

app.UseExceptionHandler(o => { });
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRouteEndpoints();

app.MapGet("/", () => "DMS Configuration Service");

app.Run();

/// <summary>
/// Triggers configuration validation. If configuration is invalid, injects a short-circuit middleware to report.
/// Returns true if the middleware was injected.
/// </summary>
bool ReportInvalidConfiguration(WebApplication app)
{
    try
    {
        // Accessing IOptions<T> forces validation
        _ = app.Services.GetRequiredService<IOptions<IdentitySettings>>().Value;
    }
    catch (OptionsValidationException ex)
    {
        app.UseMiddleware<ReportInvalidConfigurationMiddleware>(ex.Failures);
        return true;
    }
    return false;
}

void InitializeDatabase(WebApplication app)
{
    if (app.Services.GetRequiredService<IOptions<AppSettings>>().Value.DeployDatabaseOnStartup)
    {
        app.Logger.LogInformation("Running initial database deploy");
        try
        {
            var result = app
                .Services.GetRequiredService<IDatabaseDeploy>()
                .DeployDatabase(
                    app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.DatabaseConnection
                );
            if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
            {
                app.Logger.LogCritical(failure.Error, "Database Deploy Failure");
                Environment.Exit(-1);
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Database Deploy Failure");
            Environment.Exit(-1);
        }
    }
}

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
