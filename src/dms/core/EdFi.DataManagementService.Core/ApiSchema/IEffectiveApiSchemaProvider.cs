// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides access to the effective (merged) API schema documents built at startup.
/// The effective schema is the result of merging core API schemas with extension schemas.
/// </summary>
internal interface IEffectiveApiSchemaProvider
{
    /// <summary>
    /// Gets the merged API schema documents containing core and extension data.
    /// This is built once at startup and remains stable for the lifetime of the process.
    /// </summary>
    ApiSchemaDocuments Documents { get; }

    /// <summary>
    /// Gets the unique identifier for this schema configuration.
    /// This value is stable for the lifetime of the process.
    /// </summary>
    Guid SchemaId { get; }

    /// <summary>
    /// Gets whether the effective schema has been initialized.
    /// </summary>
    bool IsInitialized { get; }
}
