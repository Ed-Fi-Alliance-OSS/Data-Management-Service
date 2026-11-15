// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

internal static class DataStoreManageOpenApi
{
    public static Task Query(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken token
    )
    {
        operation.Parameters =
        [
            Parameter(
                "offset",
                new()
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = "0",
                    Default = JsonValue.Create(0),
                }
            ),
            Parameter(
                "limit",
                new()
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = "1",
                    Default = JsonValue.Create(25),
                }
            ),
            Parameter("id", new() { Type = JsonSchemaType.Integer, Format = "int32" }),
            Parameter(
                "name",
                new() { Type = JsonSchemaType.String },
                "Trimmed ASCII case-insensitive equality. Empty matches no valid managed name."
            ),
            Parameter(
                "orderBy",
                new()
                {
                    Type = JsonSchemaType.String,
                    Default = JsonValue.Create("id"),
                    Enum = new[]
                    {
                        "",
                        "id",
                        "name",
                        "dataStoreId",
                        "dataStoreName",
                        "status",
                        "databaseTemplate",
                        "databaseName",
                        "lastRefreshed",
                        "lastModifiedDate",
                    }
                        .Select(value => (JsonNode)JsonValue.Create(value)!)
                        .ToList(),
                },
                "Exact field name; empty means id."
            ),
            Parameter(
                "direction",
                new()
                {
                    Type = JsonSchemaType.String,
                    Default = JsonValue.Create("Descending"),
                    Enum = [JsonValue.Create("Ascending")!, JsonValue.Create("Descending")!],
                },
                "Exact, case-sensitive direction."
            ),
        ];
        return Task.CompletedTask;
    }

    public static Task Identifier(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken token
    )
    {
        var id = operation
            .Parameters?.OfType<OpenApiParameter>()
            .SingleOrDefault(parameter => parameter.Name == "id");
        if (id is not null)
        {
            id.Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" };
        }
        return Task.CompletedTask;
    }

    public static Task Create(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken token
    )
    {
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        AdditionalPropertiesAllowed = false,
                        Required = new HashSet<string> { "name", "databaseTemplate" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["name"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Description =
                                    "Trimmed before validation; then 1–46 ASCII letters, digits, spaces or underscores.",
                            },
                            ["databaseTemplate"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Enum = [JsonValue.Create("Minimal")!, JsonValue.Create("Sample")!],
                            },
                        },
                    },
                },
            },
        };
        if (operation.Responses?["202"] is OpenApiResponse response)
        {
            response.Content?.Clear();
            response.Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Location"] = new OpenApiHeader
                {
                    Required = true,
                    Description = "Absolute URL of the accepted managed data store.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                },
            };
        }
        return Task.CompletedTask;
    }

    private static OpenApiParameter Parameter(
        string name,
        OpenApiSchema schema,
        string? description = null
    ) =>
        new()
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Schema = schema,
            Description = description,
        };
}
