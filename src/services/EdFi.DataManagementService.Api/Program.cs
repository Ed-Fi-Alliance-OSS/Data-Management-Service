// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddServices();

builder.Services.AddLogging(builder => builder.AddConsole());
builder.Services.AddSingleton<LogAppSettingsService>();

var app = builder.Build();

app.UsePathBase("/api");
app.UseRouting();

app.MapGet("/", () => "Data Management Service");
app.MapGet("/ping", () => Results.Ok(DateTime.Now));

var appSettingsService = app.Services.GetRequiredService<LogAppSettingsService>();
appSettingsService.Log();

app.Run();

public partial class Program
{
    // Compliant solution for Sonar lint S1118
    private Program() { }
}
