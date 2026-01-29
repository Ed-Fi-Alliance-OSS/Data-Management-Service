// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Initializes backend mappings at application startup.
/// This includes loading mapping packs or performing runtime compilation,
/// and validating that database instances are provisioned for the correct schema.
/// </summary>
public interface IBackendMappingInitializer
{
    /// <summary>
    /// Initializes backend mappings for the effective schema.
    /// This may involve:
    /// - Loading .mpack files for the configured dialect/version
    /// - Runtime compilation of mapping sets
    /// - Validating database fingerprints (EffectiveSchemaHash)
    /// - Caching ResourceKeyId maps
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if initialization fails.</exception>
    Task InitializeAsync(CancellationToken cancellationToken);
}
