// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.Core.Middleware;


public class ApiSchemaLoader : IApiSchemaLoader
{
    private readonly JsonNode _apiSchemaRootNode;
    public JsonNode ApiSchemaRootNode => _apiSchemaRootNode;
    public ApiSchemaLoader(ILogger<ApiSchemaLoader> _logger)
    {
        _logger.LogInformation("ApiSchemaLoader");

        // Hardcoded and synchronous way to read the API Schema file
        _apiSchemaRootNode =
            JsonNode.Parse(File.ReadAllText($"{AppContext.BaseDirectory}/ApiSchema/DataStandard-5.0.0-ApiSchema.json")) ??
            throw new InvalidOperationException("Unable to read and parse Api Schema file.");
    }
}
