// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// JSON serialization for harness artifacts: camelCase, indented with LF-only newlines so the
/// committed artifacts diff identically on every platform, and null fields omitted so each
/// provider's inapplicable metrics disappear rather than appearing as null.
/// </summary>
public static class PerfArtifactJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T artifact) => JsonSerializer.Serialize(artifact, _options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, _options)
        ?? throw new InvalidOperationException($"Artifact JSON deserialized to null for {typeof(T).Name}.");
}
