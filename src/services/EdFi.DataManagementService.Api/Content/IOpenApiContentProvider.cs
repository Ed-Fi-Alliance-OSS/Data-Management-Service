// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.Content;

public interface IOpenApiContentProvider
{
    Task<IEnumerable<OpenApiContent>> GetContent();
}

public class DependenciesContentProvider : IOpenApiContentProvider
{
    public async Task<IEnumerable<OpenApiContent>> GetContent()
    {
        using StreamReader r = new($"{AppContext.BaseDirectory}/Content/Dependencies.json");
        JsonNode? json = await JsonNode.ParseAsync(r.BaseStream);
        var metaData = JsonSerializer.Serialize(json);
        return new List<OpenApiContent>()
        {
            new("dependencies", "Dependencies", new Lazy<string>(metaData), "", "")
        };
    }
}

public record OpenApiContent(
    string section,
    string name,
    Lazy<string> metadata,
    string basePath,
    string? relativeSectionPath = null
);
