// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Response;
using Microsoft.AspNetCore.Http;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public static class ProblemDetailsExtensions
{
    public static JsonNode ToNotFoundProblemDetails(this HttpContext context, string? correlationId = null)
    {
        var traceId = correlationId ?? context.TraceIdentifier;
        return FailureResponse.ForNotFound("The specified data could not be found.", new EdFi.DataManagementService.Core.External.Model.TraceId(traceId));
    }
}
