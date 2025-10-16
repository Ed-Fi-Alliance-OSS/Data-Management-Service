// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaGenerator.Abstractions
{
    /// <summary>
    /// Strategy interface for generating database-specific DDL scripts from ApiSchema metadata.
    /// </summary>
    public interface IDdlGeneratorStrategy
    {
        /// <summary>
        /// Generates DDL scripts for the given ApiSchema metadata.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="includeExtensions">Whether to include extensions in the DDL.</param>
        void GenerateDdl(ApiSchema apiSchema, string outputDirectory, bool includeExtensions, bool skipUnionViews = false);

        /// <summary>
        /// Generates DDL scripts for the given ApiSchema metadata with advanced options.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="outputDirectory">The directory to write output scripts to.</param>
        /// <param name="options">DDL generation options including schema mappings and feature flags.</param>
        void GenerateDdl(ApiSchema apiSchema, string outputDirectory, DdlGenerationOptions options);

        /// <summary>
        /// Generates DDL scripts as a string for the given ApiSchema metadata.
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="options">DDL generation options including schema mappings and feature flags.</param>
        /// <returns>The generated DDL script as a string.</returns>
        string GenerateDdlString(ApiSchema apiSchema, DdlGenerationOptions options);

        /// <summary>
        /// Generates DDL scripts as a string for the given ApiSchema metadata (legacy method).
        /// </summary>
        /// <param name="apiSchema">The deserialized ApiSchema metadata object.</param>
        /// <param name="includeExtensions">Whether to include extensions in the DDL.</param>
        /// <param name="skipUnionViews">Whether to skip generating union views.</param>
        /// <returns>The generated DDL script as a string.</returns>
        string GenerateDdlString(ApiSchema apiSchema, bool includeExtensions, bool skipUnionViews = false);
    }
}
