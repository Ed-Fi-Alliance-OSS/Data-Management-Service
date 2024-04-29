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
        _ = app.Services.GetRequiredService<IOptions<AppSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<ConnectionStrings>>().Value;
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
