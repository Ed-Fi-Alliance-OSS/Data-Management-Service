// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides core and extension ApiSchemas as parsed JSON
/// </summary>
public interface IApiSchemaProvider
{
    /// <summary>
    /// Core ApiSchema as parsed JSON
    /// </summary>
    JsonNode CoreApiSchemaRootNode { get; }

    /// <summary>
    /// Extension ApiSchemas as parsed JSON
    /// </summary>
    JsonNode[] ExtensionApiSchemaRootNodes { get; }
}
