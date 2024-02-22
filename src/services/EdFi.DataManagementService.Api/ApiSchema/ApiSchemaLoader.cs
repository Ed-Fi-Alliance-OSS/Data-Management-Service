// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.ApiSchema;

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
        _logger.LogDebug("Entering ApiSchemaFileLoader");

        // Hardcoded and synchronous way to read the API Schema file
        string fileLocation = $"{AppContext.BaseDirectory}/ApiSchema/DataStandard-5.0.0-ApiSchema.json";
        JsonNode? rootNodeFromFile = JsonNode.Parse(File.ReadAllText(fileLocation));

        if (rootNodeFromFile == null)
        {
            _logger.LogCritical("Unable to read and parse Api Schema file at {fileLocation}", fileLocation);
            throw new InvalidOperationException("Unable to read and parse Api Schema file.");
        }
        _apiSchemaRootNode = rootNodeFromFile;
    }
}
