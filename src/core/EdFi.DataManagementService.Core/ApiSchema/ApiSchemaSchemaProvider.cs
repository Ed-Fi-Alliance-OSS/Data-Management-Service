// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using Json.Schema;
using System.Reflection;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Api schema schema provider
/// </summary>
internal interface IApiSchemaSchemaProvider
{
    /// <summary>
    /// Provides ApiSchema schema
    /// </summary>
    JsonSchema ApiSchemaSchema { get; }
}

/// <summary>
/// Loads and parses Api schema schema json
/// </summary>
internal class ApiSchemaSchemaProvider(ILogger<ApiSchemaSchemaProvider> _logger) : IApiSchemaSchemaProvider
{
    private readonly Lazy<JsonSchema> _apiSchemaSchema =
        new(() =>
        {
            _logger.LogDebug("Entering ApiSchemaSchemaProvider");

            string schemaContent = File.ReadAllText(
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "ApiSchema", "ApiSchema_Schema.json")
            );
            var schema = JsonSchema.FromText(schemaContent);

            return schema;
        });

    public JsonSchema ApiSchemaSchema => _apiSchemaSchema.Value;
}
