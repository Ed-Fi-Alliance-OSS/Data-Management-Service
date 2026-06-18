// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

public static class ChangeQueriesOpenApiDocumentTestHelper
{
    public static JsonObject MinimalOpenApiDocument(string title, bool includeServers = false)
    {
        JsonObject document = new()
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = "5.0.0" },
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject { ["schemas"] = new JsonObject() },
            ["tags"] = new JsonArray(),
        };

        if (includeServers)
        {
            document["servers"] = new JsonArray();
        }

        return document;
    }

    public static JsonObject ChangeQueriesOpenApiDocument(
        string title,
        string? availableChangeVersionsSummary = null,
        bool includeAvailableChangeVersionsPath = true,
        bool includeServers = false
    )
    {
        JsonObject document = MinimalOpenApiDocument(title, includeServers);
        document["tags"] = new JsonArray(
            new JsonObject { ["name"] = "changeQueries", ["description"] = "Change Queries" }
        );

        if (includeAvailableChangeVersionsPath)
        {
            JsonObject getOperation = new()
            {
                ["description"] = "availableChangeVersions get description",
                ["tags"] = new JsonArray("changeQueries"),
                ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "OK" } },
            };

            if (availableChangeVersionsSummary is not null)
            {
                getOperation["summary"] = availableChangeVersionsSummary;
            }

            document["paths"] = new JsonObject
            {
                ["/availableChangeVersions"] = new JsonObject { ["get"] = getOperation },
            };
        }

        return document;
    }
}
