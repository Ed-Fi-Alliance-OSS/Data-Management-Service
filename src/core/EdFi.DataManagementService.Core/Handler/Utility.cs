// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Handler;

public static class Utility
{
    /// <summary>
    /// Formats a error result string from the given error information and traceId
    /// </summary>
    public static JsonNode? ToJsonError(string errorInfo, TraceId traceId)
    {
        return new JsonObject
        {
            ["error"] = errorInfo,
            ["correlationId"] = traceId.Value,
        };
    }
}
