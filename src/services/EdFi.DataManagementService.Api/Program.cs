// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Configuration;
using EdFi.DataManagementService.Api.Infrastructure;
using Microsoft.Extensions.Options;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.AddServices();

var app = builder.Build();

app.UsePathBase("/api");
app.UseMiddleware<LoggingMiddleware>();
app.UseRouting();
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok("Data Management Service"));
app.MapGet("/ping", () => Results.Ok(DateTime.Now));

app.MapGet("/LogAppSettings", (IOptions<AppSettings> options, ILogger<Program> logger) =>
{
    logger.LogDebug($"Log while debugging");
    logger.LogInformation(message: JsonSerializer.Serialize(options.Value));
    return Results.Ok("Successfully logged Application Settings");
});

app.Run();

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
