// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Selects the manifest-backed ApiSchema workspace, bound from the <c>AppSettings:ApiSchema</c>
/// section.
/// </summary>
public class ApiSchemaSettings
{
    /// <summary>
    /// If true, loads manifest-backed ApiSchema workspace content from <see cref="Path"/>.
    /// Otherwise, loads the bundled manifest-backed ApiSchema workspace from the application output.
    /// </summary>
    public bool UseCustomPath { get; set; }

    /// <summary>
    /// Provides the file system path for a runtime ApiSchema workspace containing
    /// bootstrap-api-schema-manifest.json and its declared core and extension schema files.
    /// </summary>
    public string? Path { get; set; }
}
