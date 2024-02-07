// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UsePathBase("/api");
app.UseRouting();

app.MapGet("/", () => "Data Management Service");
app.MapGet("/ping", () => Results.Ok(new { StatusCode = 200, CurrentDateTime = DateTime.Now.ToString() }));

app.Run();
