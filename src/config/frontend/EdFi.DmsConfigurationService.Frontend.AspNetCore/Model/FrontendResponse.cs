// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;

public class FrontendResponse
{
    public int StatusCode { get; set; }
    public JsonNode? Body { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? LocationHeaderPath { get; set; } = null;
    public string? ContentType { get; set; } = "application/json";
}
