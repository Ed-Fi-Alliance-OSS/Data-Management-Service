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
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    IApiSchemaInputNormalizer inputNormalizer,
    EffectiveSchemaSetBuilder effectiveSchemaSetBuilder,
    ILogger<LoadAndBuildEffectiveSchemaTask> logger
) : IDmsStartupTask
{
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = effectiveApiSchemaProvider;
    private readonly IEffectiveSchemaSetProvider _effectiveSchemaSetProvider = effectiveSchemaSetProvider;
    private readonly IApiSchemaInputNormalizer _inputNormalizer = inputNormalizer;
    private readonly EffectiveSchemaSetBuilder _effectiveSchemaSetBuilder = effectiveSchemaSetBuilder;
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

        // Step 3: Build the authoritative effective schema set once for all later startup consumers.
        _logger.LogDebug("Building effective schema set");
        var effectiveSchemaSet = _effectiveSchemaSetBuilder.Build(normalizedNodes);
        var effectiveSchemaInfo = effectiveSchemaSet.EffectiveSchema;

        _logger.LogInformation("Effective schema hash: {Hash}", effectiveSchemaInfo.EffectiveSchemaHash);

        if (effectiveSchemaInfo.ResourceKeyCount > 0)
        {
            _logger.LogInformation(
                "Resource key seeds: {SeedCount} entries, hash: {Hash}",
                effectiveSchemaInfo.ResourceKeyCount,
                Convert.ToHexStringLower(effectiveSchemaInfo.ResourceKeySeedHash)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 4: Build the effective API schema documents and cache the authoritative schema set.
        _logger.LogInformation("Building effective schema and priming caches");
        _effectiveApiSchemaProvider.Initialize(normalizedNodes);
        _effectiveSchemaSetProvider.Initialize(effectiveSchemaSet);

        _logger.LogInformation(
            "Effective API schema initialization complete. SchemaId: {SchemaId}",
            _effectiveApiSchemaProvider.SchemaId
        );

        return Task.CompletedTask;
    }
}
