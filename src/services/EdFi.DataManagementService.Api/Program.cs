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
    try
    {
        // Accessing IOptions<T> forces validation
#pragma warning disable S1481 // Unused local variables should be removed
        var appSettings = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        var connectionStrings = app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value;
#pragma warning restore S1481 // Unused local variables should be removed
    }
    catch (OptionsValidationException ex)
    {
        app.UseMiddleware<InvalidConfigurationMiddleware>(ex.Failures);
    }
}


public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
