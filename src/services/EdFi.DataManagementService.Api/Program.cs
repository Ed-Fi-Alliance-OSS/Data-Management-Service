// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Infrastructure;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddServices();

var app = builder.Build();

app.UseMiddleware<LoggingMiddleware>();

InjectInvalidConfigurationMiddleware(app);

app.UseRouting();
app.UseRateLimiter();
app.MapRouteEndpoints();

app.Run();


void InjectInvalidConfigurationMiddleware(WebApplication app)
{
    List<string> errors = [];

    var appSettings = app.Services.GetRequiredService<IOptions<AppSettings>>();
    var connectionStrings = app.Services.GetRequiredService<IOptions<ConnectionStrings>>();

    errors.AddRange(appSettings.Value.GetCriticalErrors());
    errors.AddRange(connectionStrings.Value.GetCriticalErrors());


    if (errors.Any())
    {
        app.UseMiddleware<InvalidConfigurationMiddleware>(errors);
    }
}


public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
