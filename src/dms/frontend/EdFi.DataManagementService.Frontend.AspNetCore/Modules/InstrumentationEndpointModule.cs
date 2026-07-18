// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Diagnostics;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// DMS-1236 instrumentation endpoints (only mapped when RequestTimings:Enabled):
/// GET /instrumentation returns the aggregated phase timing snapshot as JSON;
/// POST /instrumentation/reset returns the final snapshot and clears the aggregates
/// (call it right before a test run to open a clean measurement window).
/// </summary>
public class InstrumentationEndpointModule(IOptions<RequestTimingOptions> options) : IEndpointModule
{
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        endpoints.MapGet("/instrumentation", GetSnapshot);
        endpoints.MapPost("/instrumentation/reset", ResetSnapshot);
    }

    internal static IResult GetSnapshot()
    {
        return Results.Json(RequestTimingRegistry.Snapshot(), _serializerOptions);
    }

    internal static IResult ResetSnapshot()
    {
        return Results.Json(RequestTimingRegistry.Reset(), _serializerOptions);
    }
}
