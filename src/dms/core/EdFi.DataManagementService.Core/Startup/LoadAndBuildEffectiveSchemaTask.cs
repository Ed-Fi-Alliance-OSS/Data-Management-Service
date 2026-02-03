// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that loads, validates, and builds the effective API schema.
/// This task runs early in the startup sequence to ensure schemas are available
/// before any request processing begins.
/// </summary>
internal class LoadAndBuildEffectiveSchemaTask(
    IApiSchemaProvider apiSchemaProvider,
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    IApiSchemaInputNormalizer inputNormalizer,
    IEffectiveSchemaHashProvider hashProvider,
    IResourceKeySeedProvider seedProvider,
    ILogger<LoadAndBuildEffectiveSchemaTask> logger
) : IDmsStartupTask
{
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = effectiveApiSchemaProvider;
    private readonly IApiSchemaInputNormalizer _inputNormalizer = inputNormalizer;
    private readonly IEffectiveSchemaHashProvider _hashProvider = hashProvider;
    private readonly IResourceKeySeedProvider _seedProvider = seedProvider;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public string Name => "Load and Build Effective Schema";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Loading API schemas from configured source");

        // Step 1: Load raw schema nodes (this validates the schemas)
        ApiSchemaDocumentNodes rawNodes;
        try
        {
            rawNodes = _apiSchemaProvider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to load API schemas. DMS cannot start without valid schemas.");
            throw new InvalidOperationException("API schema loading failed", ex);
        }

        // Check if schema is valid
        if (!_apiSchemaProvider.IsSchemaValid)
        {
            var failures = _apiSchemaProvider.ApiSchemaFailures;
            _logger.LogCritical("API schema validation failed with {FailureCount} error(s)", failures.Count);
            foreach (var failure in failures)
            {
                _logger.LogCritical(
                    "Schema validation failure: [{Type}] {Message}",
                    failure.FailureType,
                    failure.Message
                );
            }
            throw new InvalidOperationException(
                $"API schema validation failed with {failures.Count} error(s)"
            );
        }

        _logger.LogInformation("API schemas loaded and validated successfully");

        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: Normalize schema inputs
        _logger.LogDebug("Normalizing schema inputs");
        var normalizationResult = _inputNormalizer.Normalize(rawNodes);
        var normalizedNodes = normalizationResult switch
        {
            ApiSchemaNormalizationResult.SuccessResult success => success.NormalizedNodes,
            ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult failure =>
                throw new InvalidOperationException(
                    $"Schema normalization failed for '{failure.SchemaSource}': {failure.Details}"
                ),
            ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult failure =>
                throw new InvalidOperationException(
                    $"apiSchemaVersion mismatch in '{failure.SchemaSource}': expected '{failure.ExpectedVersion}', got '{failure.ActualVersion}'"
                ),
            ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult failure =>
                throw new InvalidOperationException(
                    $"Duplicate projectEndpointName(s) found: {string.Join("; ", failure.Collisions.Select(c => $"'{c.ProjectEndpointName}' in [{string.Join(", ", c.ConflictingSources)}]"))}"
                ),
            _ => throw new InvalidOperationException("Unknown normalization result"),
        };

        cancellationToken.ThrowIfCancellationRequested();

        // Step 3: Compute effective schema hash (no-op for now, future DMS-925)
        _logger.LogDebug("Computing effective schema hash");
        var effectiveSchemaHash = _hashProvider.ComputeHash(normalizedNodes);
        if (!string.IsNullOrEmpty(effectiveSchemaHash))
        {
            _logger.LogInformation("Effective schema hash: {Hash}", effectiveSchemaHash);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: Derive resource key seeds (no-op for now, future DMS-926)
        _logger.LogDebug("Deriving resource key seeds");
        var seeds = _seedProvider.GetSeeds(normalizedNodes);
        if (seeds.Count > 0)
        {
            var seedHash = _seedProvider.ComputeSeedHash(seeds);
            _logger.LogInformation(
                "Resource key seeds: {SeedCount} entries, hash: {Hash}",
                seeds.Count,
                seedHash
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 5: Build the effective schema and prime caches
        _logger.LogInformation("Building effective schema and priming caches");
        _effectiveApiSchemaProvider.Initialize(normalizedNodes);

        _logger.LogInformation(
            "Effective API schema initialization complete. SchemaId: {SchemaId}",
            _effectiveApiSchemaProvider.SchemaId
        );

        return Task.CompletedTask;
    }
}
