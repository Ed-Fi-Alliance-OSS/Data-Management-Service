// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Json.Schema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Api schema schema provider
/// </summary>
internal interface IJsonSchemaForApiSchemaProvider
{
    /// <summary>
    /// Provides JsonSchema for ApiSchema
    /// </summary>
    JsonSchema JsonSchemaForApiSchema { get; }
}

/// <summary>
/// Loads and parses JsonSchema for ApiSchema
/// </summary>
internal class JsonSchemaForApiSchemaProvider(ILogger<JsonSchemaForApiSchemaProvider> _logger)
    : IJsonSchemaForApiSchemaProvider
{
    private readonly Lazy<JsonSchema> _jsonSchemaForApiSchema = new(() =>
    {
        _logger.LogDebug("Entering _jsonSchemaForApiSchema");

        string schemaContent = File.ReadAllText(
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                "ApiSchema",
                "JsonSchemaForApiSchema.json"
            )
        );
        var schema = JsonSchema.FromText(schemaContent);

        return schema;
    });

    public JsonSchema JsonSchemaForApiSchema => _jsonSchemaForApiSchema.Value;
}
