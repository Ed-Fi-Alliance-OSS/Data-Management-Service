// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache;

[JsonConverter(typeof(DocumentCacheDiagnosticSnapshotJsonConverter))]
public sealed record DocumentCacheDiagnosticSnapshot
{
    public DocumentCacheDiagnosticSnapshot(
        IEnumerable<DocumentCacheTargetDiagnosticSnapshot> targets,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(targets);

        Targets = targets.ToImmutableArray();
        ObservedAt = observedAt;
    }

    public ImmutableArray<DocumentCacheTargetDiagnosticSnapshot> Targets { get; }

    public DateTimeOffset ObservedAt { get; }

    public static DocumentCacheDiagnosticSnapshot FromRegistrySnapshot(
        DocumentCacheTargetRegistrySnapshot registrySnapshot
    )
    {
        ArgumentNullException.ThrowIfNull(registrySnapshot);

        return new(
            registrySnapshot.Targets.Select(DocumentCacheTargetDiagnosticSnapshot.FromObservation),
            registrySnapshot.ObservedAt
        );
    }
}

[JsonConverter(typeof(DocumentCacheTargetDiagnosticSnapshotJsonConverter))]
public sealed record DocumentCacheTargetDiagnosticSnapshot
{
    private DocumentCacheTargetDiagnosticSnapshot(
        DocumentCacheTargetKey targetKey,
        DocumentCacheTargetResolutionState resolutionState,
        DocumentCacheTargetEligibilityState eligibilityState,
        DocumentCacheTargetEffectiveSettings effectiveSettings,
        DocumentCacheTargetContextGeneration? generation,
        RelationalProviderToken? providerToken,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheLifecycleObservation? lifecycle,
        DocumentCacheInventoryValidationResult? inventory,
        DocumentCacheEnqueueTriggerValidationResult? enqueueTrigger,
        DocumentCacheSqlServerPrerequisiteDetails? sqlServerPrerequisites,
        DocumentCacheResolutionRetryState? retryState,
        IEnumerable<DocumentCacheTargetDiagnostic> diagnostics
    )
    {
        TargetKey = targetKey;
        ResolutionState = resolutionState;
        EligibilityState = eligibilityState;
        EffectiveSettings = effectiveSettings;
        Generation = generation;
        ProviderToken = providerToken;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Lifecycle = lifecycle;
        Inventory = inventory;
        EnqueueTrigger = enqueueTrigger;
        SqlServerPrerequisites = sqlServerPrerequisites;
        RetryState = retryState;
        Diagnostics = diagnostics.ToImmutableArray();
    }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCacheTargetResolutionState ResolutionState { get; }

    public DocumentCacheTargetEligibilityState EligibilityState { get; }

    public DocumentCacheTargetEffectiveSettings EffectiveSettings { get; }

    public DocumentCacheTargetContextGeneration? Generation { get; }

    public RelationalProviderToken? ProviderToken { get; }

    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    public DocumentCacheLifecycleObservation? Lifecycle { get; }

    public DocumentCacheInventoryValidationResult? Inventory { get; }

    public DocumentCacheEnqueueTriggerValidationResult? EnqueueTrigger { get; }

    public DocumentCacheSqlServerPrerequisiteDetails? SqlServerPrerequisites { get; }

    public DocumentCacheResolutionRetryState? RetryState { get; }

    public ImmutableArray<DocumentCacheTargetDiagnostic> Diagnostics { get; }

    public static DocumentCacheTargetDiagnosticSnapshot FromObservation(
        DocumentCacheTargetObservation observation
    )
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new(
            observation.TargetKey,
            observation.ResolutionState,
            observation.EligibilityState,
            observation.EffectiveSettings,
            observation.Generation,
            observation.ProviderToken,
            observation.PhysicalSourceFingerprint,
            observation.Lifecycle,
            observation.Inventory,
            observation.EnqueueTrigger,
            observation.SqlServerPrerequisites,
            observation.RetryState,
            observation.Diagnostics
        );
    }
}

public interface IDocumentCacheDiagnosticSnapshotProvider
{
    DocumentCacheDiagnosticSnapshot CurrentSnapshot { get; }
}

public sealed class DocumentCacheDiagnosticSnapshotProvider(IDocumentCacheTargetRegistry targetRegistry)
    : IDocumentCacheDiagnosticSnapshotProvider
{
    public DocumentCacheDiagnosticSnapshot CurrentSnapshot =>
        DocumentCacheDiagnosticSnapshot.FromRegistrySnapshot(targetRegistry.CurrentSnapshot);
}

internal sealed class DocumentCacheDiagnosticSnapshotJsonConverter
    : JsonConverter<DocumentCacheDiagnosticSnapshot>
{
    public override DocumentCacheDiagnosticSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => throw DocumentCacheDiagnosticSnapshotJsonBoundary.NotSupported();

    public override void Write(
        Utf8JsonWriter writer,
        DocumentCacheDiagnosticSnapshot value,
        JsonSerializerOptions options
    ) => throw DocumentCacheDiagnosticSnapshotJsonBoundary.NotSupported();
}

internal sealed class DocumentCacheTargetDiagnosticSnapshotJsonConverter
    : JsonConverter<DocumentCacheTargetDiagnosticSnapshot>
{
    public override DocumentCacheTargetDiagnosticSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => throw DocumentCacheDiagnosticSnapshotJsonBoundary.NotSupported();

    public override void Write(
        Utf8JsonWriter writer,
        DocumentCacheTargetDiagnosticSnapshot value,
        JsonSerializerOptions options
    ) => throw DocumentCacheDiagnosticSnapshotJsonBoundary.NotSupported();
}

internal static class DocumentCacheDiagnosticSnapshotJsonBoundary
{
    public static NotSupportedException NotSupported() =>
        new(
            "DocumentCache diagnostic snapshots are internal 18-01 domain state. Public diagnostic JSON is deferred to the 18-06 health/status contract."
        );
}
