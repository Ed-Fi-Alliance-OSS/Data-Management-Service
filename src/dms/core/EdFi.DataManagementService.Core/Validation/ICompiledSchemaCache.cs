// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using Json.Schema;

namespace EdFi.DataManagementService.Core.Validation;

/// <summary>
/// Provides caching for compiled JSON Schemas keyed by resource, method, and schema reload identifier.
/// </summary>
internal interface ICompiledSchemaCache
{
    /// <summary>
    /// Retrieves a compiled schema from the cache or adds it using the provided factory.
    /// </summary>
    JsonSchema GetOrAdd(
        ProjectName projectName,
        ResourceName resourceName,
        RequestMethod method,
        Guid reloadId,
        Func<JsonSchema> schemaFactory
    );

    /// <summary>
    /// Pre-compiles schemas for the supplied documents.
    /// </summary>
    void Prime(ApiSchemaDocuments documents, Guid reloadId);
}
