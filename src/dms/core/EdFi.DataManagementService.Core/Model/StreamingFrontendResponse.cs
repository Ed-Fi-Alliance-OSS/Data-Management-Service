// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EdFi.DataManagementService.Core.External.Frontend;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// Frontend response that streams its payload directly to the caller.
/// </summary>
internal sealed record StreamingFrontendResponse(
    int StatusCode,
    Func<Stream, CancellationToken, Task> WriteBodyAsync,
    Dictionary<string, string> Headers,
    string? LocationHeaderPath = null,
    string? ContentType = "application/json"
) : IStreamableFrontendResponse
{
    public JsonNode? Body => null;
}
