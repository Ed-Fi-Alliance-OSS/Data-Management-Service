// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Loads and normalizes ApiSchema.json files from explicit file paths.
/// This is the library-first entry point for CLI and test harness usage,
/// independent of DMS runtime configuration.
/// </summary>
public interface IApiSchemaFileLoader
{
    /// <summary>
    /// Loads ApiSchema.json files from the specified paths, validates them,
    /// and returns normalized schema nodes suitable for hashing and model derivation.
    /// </summary>
    /// <param name="coreSchemaPath">Path to the core ApiSchema.json file.</param>
    /// <param name="extensionSchemaPaths">Paths to extension ApiSchema.json files (may be empty).</param>
    /// <returns>A result indicating success with normalized nodes, or failure with details.</returns>
    ApiSchemaFileLoadResult Load(string coreSchemaPath, IReadOnlyList<string> extensionSchemaPaths);
}
