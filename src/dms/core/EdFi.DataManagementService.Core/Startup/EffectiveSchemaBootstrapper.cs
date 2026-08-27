// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

internal sealed class EffectiveSchemaBootstrapper(
    IApiSchemaProvider apiSchemaProvider,
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    IApiSchemaInputNormalizer inputNormalizer,
    EffectiveSchemaSetBuilder effectiveSchemaSetBuilder,
    ILogger<EffectiveSchemaBootstrapper> logger
) : IEffectiveSchemaBootstrapper
{
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = effectiveApiSchemaProvider;
    private readonly IEffectiveSchemaSetProvider _effectiveSchemaSetProvider = effectiveSchemaSetProvider;
    private readonly IApiSchemaInputNormalizer _inputNormalizer = inputNormalizer;
    private readonly EffectiveSchemaSetBuilder _effectiveSchemaSetBuilder = effectiveSchemaSetBuilder;
    private readonly ILogger _logger = logger;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Loading API schemas from configured source");

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

        _logger.LogInformation("Building effective schema and priming caches");
        _effectiveSchemaSetProvider.Initialize(effectiveSchemaSet);
        _effectiveApiSchemaProvider.Initialize(normalizedNodes);

        _logger.LogInformation("Effective API schema initialization complete");

        return Task.CompletedTask;
    }
}
