// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.Core.ApiSchema;

/// <summary>
/// Loads and parses the ApiSchema.json from a file.
/// </summary>
public class ApiSchemaFileLoader : IApiSchemaProvider
{
    private readonly JsonNode _apiSchemaRootNode;

    /// <summary>
    /// The parsed ApiSchema.json file
    /// </summary>
    public JsonNode ApiSchemaRootNode => _apiSchemaRootNode;

    public ApiSchemaFileLoader(ILogger<ApiSchemaFileLoader> _logger)
    {
        var jsonContent = File.ReadAllText("/home/brad/work/tanager/Data-Management-Service/src/services/EdFi.DataManagementService.Api/ds-5.0-api-schema-authoritative.json");

        JsonNode? rootNodeFromFile = JsonNode.Parse(jsonContent);
        if (rootNodeFromFile == null)
        {
            _logger.LogCritical("Unable to read and parse Api Schema file");
            throw new InvalidOperationException("Unable to read and parse Api Schema file.");
        }
        _apiSchemaRootNode = rootNodeFromFile;
    }
}
