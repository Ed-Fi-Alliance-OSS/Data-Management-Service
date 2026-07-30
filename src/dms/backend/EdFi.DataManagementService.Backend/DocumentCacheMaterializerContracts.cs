// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Identifies a resolved process-local DocumentCache projection target. Target selection and
/// lifecycle eligibility are owned by DocumentCache target-resolution services, not by the materializer.
/// </summary>
public sealed record DocumentCacheProjectionTargetKey
{
    public DocumentCacheProjectionTargetKey(string tenantKey, DataStoreId dataStoreId)
    {
        TenantKey = tenantKey ?? throw new ArgumentNullException(nameof(tenantKey));
        DataStoreId =
            dataStoreId.Value > 0
                ? dataStoreId
                : throw new ArgumentOutOfRangeException(
                    nameof(dataStoreId),
                    dataStoreId,
                    "DocumentCache projection target DataStoreId must be positive."
                );
    }

    /// <summary>
    /// The normalized tenant key from the configured <c>DocumentCache:Targets</c> entry. Empty is
    /// the default tenant.
    /// </summary>
    public string TenantKey { get; }

    /// <summary>
    /// The configured data store id for this resolved projection target.
    /// </summary>
    public DataStoreId DataStoreId { get; }

    public override string ToString() => $"{TenantKey}/{DataStoreId.Value}";
}

/// <summary>
/// Documents the target-resolution precondition for materialization. A resolved target context may
/// be created only after the target database's <c>dms.EffectiveSchema</c> fingerprint and
/// <c>dms.ResourceKey</c> seed have been validated against the selected mapping set.
/// </summary>
public enum DocumentCacheMaterializationTargetValidation
{
    EffectiveSchemaAndResourceKeySeedValidated = 1,
}

internal sealed record DocumentCacheMaterializationTargetDataStore
{
    public DocumentCacheMaterializationTargetDataStore(string connectionString)
    {
        ConnectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException(
                "DocumentCache materialization target data store connection string must not be blank.",
                nameof(connectionString)
            )
            : connectionString;
    }

    public string ConnectionString { get; }
}

/// <summary>
/// Resolved target inputs consumed by one materialization call. The materializer uses the supplied
/// mapping set and target data-store connection metadata but does not resolve, validate, or refresh
/// <c>DocumentCache:Targets</c>, re-read <c>dms.EffectiveSchema</c>, or revalidate
/// <c>dms.ResourceKey</c> seed compatibility per document. Provider adapters use the bound target
/// data-store metadata for background projector and direct-fill contexts that do not have ambient HTTP
/// request data-store selection.
/// </summary>
public sealed record DocumentCacheMaterializationTargetContext
{
    public DocumentCacheMaterializationTargetContext(
        DocumentCacheProjectionTargetKey targetKey,
        MappingSet mappingSet,
        DocumentCacheMaterializationTargetValidation targetValidation
    )
    {
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        TargetValidation = RequireValidated(targetValidation, nameof(targetValidation));
        TargetDataStore = null;
    }

    public DocumentCacheMaterializationTargetContext(
        DocumentCacheProjectionTargetKey targetKey,
        MappingSet mappingSet,
        DocumentCacheMaterializationTargetValidation targetValidation,
        string targetConnectionString
    )
    {
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        TargetValidation = RequireValidated(targetValidation, nameof(targetValidation));
        TargetDataStore = new DocumentCacheMaterializationTargetDataStore(targetConnectionString);
    }

    /// <summary>
    /// Target identity used only for bounded diagnostics.
    /// </summary>
    public DocumentCacheProjectionTargetKey TargetKey { get; }

    /// <summary>
    /// The already-selected mapping set for this target database.
    /// </summary>
    public MappingSet MappingSet { get; }

    /// <summary>
    /// Marker that the target resolver selected <see cref="MappingSet" /> for
    /// <see cref="TargetKey" /> after database fingerprint and resource-key seed validation.
    /// </summary>
    public DocumentCacheMaterializationTargetValidation TargetValidation { get; }

    internal DocumentCacheMaterializationTargetDataStore? TargetDataStore { get; }

    private static DocumentCacheMaterializationTargetValidation RequireValidated(
        DocumentCacheMaterializationTargetValidation value,
        string parameterName
    )
    {
        if (value != DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "DocumentCache materialization requires a target context validated by EffectiveSchema and ResourceKey seed checks."
            );
        }

        return value;
    }
}

/// <summary>
/// Caller-local purpose included in bounded diagnostics. It is not a cache-write, lifecycle, or
/// authorization decision.
/// </summary>
public enum DocumentCacheMaterializationPurpose
{
    /// <summary>
    /// Materialization requested by the asynchronous durable-work projector.
    /// </summary>
    DurableWorkProjection = 1,

    /// <summary>
    /// Materialization requested opportunistically after a relational read fallback.
    /// </summary>
    DirectFill = 2,

    /// <summary>
    /// Materialization requested by a deterministic fixture/test harness.
    /// </summary>
    Fixture = 3,
}

/// <summary>
/// Request to materialize the latest coherent canonical source for one internal document id.
/// </summary>
public sealed record DocumentCacheMaterializationRequest
{
    public DocumentCacheMaterializationRequest(
        DocumentCacheMaterializationTargetContext targetContext,
        long documentId,
        long? selectedRequiredContentVersion,
        DocumentCacheMaterializationPurpose purpose,
        CancellationToken cancellationToken
    )
    {
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        DocumentId = DocumentCacheMaterializerGuards.RequirePositive(documentId, nameof(documentId));
        if (selectedRequiredContentVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedRequiredContentVersion),
                selectedRequiredContentVersion,
                "Selected durable-work RequiredContentVersion must be positive when supplied."
            );
        }

        SelectedRequiredContentVersion = selectedRequiredContentVersion;
        Purpose = DocumentCacheMaterializerGuards.RequireDefined(
            purpose,
            nameof(purpose),
            "Unsupported materialization purpose."
        );
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// The resolved projection target and mapping set. This request does not cause target resolution.
    /// </summary>
    public DocumentCacheMaterializationTargetContext TargetContext { get; }

    /// <summary>
    /// Internal canonical document identity. The materializer contract never accepts a public
    /// <c>DocumentUuid</c> lookup key.
    /// </summary>
    public long DocumentId { get; }

    /// <summary>
    /// Optional worker-local durable-work version selected by the caller. It is diagnostic context
    /// only and never gates hydration or becomes returned current-source evidence.
    /// </summary>
    public long? SelectedRequiredContentVersion { get; }

    /// <summary>
    /// Caller-local diagnostic purpose for this materialization attempt.
    /// </summary>
    public DocumentCacheMaterializationPurpose Purpose { get; }

    /// <summary>
    /// Cancellation for the materialization attempt. Cancellation uses the ordinary task exception flow.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Cache-row candidate produced from one coherent canonical source observation. Later cache-writer
/// stories decide whether to persist or acknowledge it.
/// </summary>
public sealed record DocumentCacheMaterializationCandidate
{
    public DocumentCacheMaterializationCandidate(
        long documentId,
        DocumentUuid documentUuid,
        string projectName,
        string resourceName,
        string resourceVersion,
        long contentVersion,
        DateTimeOffset lastModifiedAt,
        string streamEtag,
        JsonObject documentJson
    )
    {
        DocumentId = DocumentCacheMaterializerGuards.RequirePositive(documentId, nameof(documentId));
        DocumentUuid = documentUuid;
        ProjectName = RequireNonBlank(projectName, nameof(projectName));
        ResourceName = RequireNonBlank(resourceName, nameof(resourceName));
        ResourceVersion = RequireNonBlank(resourceVersion, nameof(resourceVersion));
        ContentVersion = DocumentCacheMaterializerGuards.RequirePositive(
            contentVersion,
            nameof(contentVersion)
        );
        LastModifiedAt = lastModifiedAt;
        StreamEtag = RequireNonBlank(streamEtag, nameof(streamEtag));
        DocumentJson = documentJson ?? throw new ArgumentNullException(nameof(documentJson));
    }

    public long DocumentId { get; }

    public DocumentUuid DocumentUuid { get; }

    public string ProjectName { get; }

    public string ResourceName { get; }

    public string ResourceVersion { get; }

    public long ContentVersion { get; }

    public DateTimeOffset LastModifiedAt { get; }

    public string StreamEtag { get; }

    public JsonObject DocumentJson { get; }

    private static string RequireNonBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} must not be blank.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// Result of a per-document materialization attempt. Mapping/target defects and projection
/// invariants are deterministic exceptions, not ordinary per-document outcomes.
/// </summary>
public abstract record DocumentCacheMaterializationResult
{
    private DocumentCacheMaterializationResult() { }

    /// <summary>
    /// A coherent cache-row candidate was materialized.
    /// </summary>
    public sealed record Success : DocumentCacheMaterializationResult
    {
        public Success(DocumentCacheMaterializationCandidate candidate)
        {
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        }

        public DocumentCacheMaterializationCandidate Candidate { get; }
    }

    /// <summary>
    /// The canonical <c>dms.Document</c> source row was absent.
    /// </summary>
    public sealed record MissingSource : DocumentCacheMaterializationResult
    {
        private MissingSource() { }

        public static MissingSource Instance { get; } = new();
    }

    /// <summary>
    /// Canonical source metadata changed between the first observation and the coherence check.
    /// </summary>
    public sealed record SourceChangedDuringHydration : DocumentCacheMaterializationResult
    {
        private SourceChangedDuringHydration() { }

        public static SourceChangedDuringHydration Instance { get; } = new();
    }
}

/// <summary>
/// Caller-agnostic materializer for producing a DocumentCache candidate from canonical relational source.
/// </summary>
public interface IDocumentCacheMaterializer
{
    Task<DocumentCacheMaterializationResult> MaterializeAsync(DocumentCacheMaterializationRequest request);
}

/// <summary>
/// Bounded projection-processing invariant reasons. These describe corrupted source/projection
/// state or materialization bugs for one document and produce no cache candidate.
/// </summary>
public enum DocumentCacheProjectionProcessingFailureReason
{
    StableSourceBodyMissing = 1,
    DocumentJsonNotObject = 2,
    DocumentJsonIdMismatch = 3,
    DocumentJsonLastModifiedDateMismatch = 4,
    DocumentJsonContainsEtag = 5,
}

/// <summary>
/// Bounded target/mapping reasons that make the resolved projection target unsafe to process.
/// These are not per-document materialization outcomes.
/// </summary>
public enum DocumentCacheTargetMappingFailureReason
{
    ResourceKeyMissingFromMappingSet = 1,
    ReadPlanMissing = 2,
    UnsupportedResourceStorageKind = 4,
    ConcreteResourceModelMissing = 5,
    ConcreteResourceModelMismatch = 6,
    ResourceKeyMetadataMismatch = 7,
    ReadPlanMetadataMismatch = 8,
}

/// <summary>
/// Sanitized diagnostic context shared by deterministic materializer failures. It deliberately
/// carries target/document/resource identifiers, not <c>DocumentJson</c>, authorization data, or
/// cache/write decisions.
/// </summary>
public sealed record DocumentCacheMaterializerFailureMetadata
{
    public DocumentCacheMaterializerFailureMetadata(
        DocumentCacheProjectionTargetKey targetKey,
        MappingSetKey mappingSetKey,
        DocumentCacheMaterializationPurpose purpose,
        long documentId
    )
    {
        TargetKey = targetKey ?? throw new ArgumentNullException(nameof(targetKey));
        MappingSetKey = mappingSetKey;
        Purpose = DocumentCacheMaterializerGuards.RequireDefined(
            purpose,
            nameof(purpose),
            "Unsupported materialization purpose."
        );
        DocumentId = DocumentCacheMaterializerGuards.RequirePositive(documentId, nameof(documentId));
    }

    public DocumentCacheProjectionTargetKey TargetKey { get; }

    public MappingSetKey MappingSetKey { get; }

    public DocumentCacheMaterializationPurpose Purpose { get; }

    public long DocumentId { get; }

    public long? SelectedRequiredContentVersion { get; init; }

    public short? ResourceKeyId { get; init; }

    public string? ProjectName { get; init; }

    public string? ResourceName { get; init; }

    public string? ResourceVersion { get; init; }
}

/// <summary>
/// Thrown for deterministic per-document projection-processing failures. Callers should use their
/// existing failure/backoff path and leave durable work visible.
/// </summary>
public sealed class DocumentCacheProjectionProcessingException : Exception
{
    public DocumentCacheProjectionProcessingException(
        DocumentCacheProjectionProcessingFailureReason reason,
        DocumentCacheMaterializerFailureMetadata metadata
    )
        : base(
            BuildMessage(
                DocumentCacheMaterializerGuards.RequireDefined(
                    reason,
                    nameof(reason),
                    "Unsupported projection-processing failure reason."
                ),
                metadata
            )
        )
    {
        Reason = reason;
        FailureMetadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public DocumentCacheProjectionProcessingFailureReason Reason { get; }

    public DocumentCacheMaterializerFailureMetadata FailureMetadata { get; }

    private static string BuildMessage(
        DocumentCacheProjectionProcessingFailureReason reason,
        DocumentCacheMaterializerFailureMetadata metadata
    )
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return $"DocumentCache materialization projection failure '{reason}' for target "
            + $"'{DocumentCacheMaterializerDiagnosticFormatting.FormatTargetKey(metadata.TargetKey)}', mapping set "
            + $"'{DocumentCacheMaterializerDiagnosticFormatting.FormatMappingSetKey(metadata.MappingSetKey)}', "
            + $"document id {metadata.DocumentId}.";
    }
}

/// <summary>
/// Thrown when the resolved target mapping set cannot safely materialize a document. This is target
/// fatal, not an ordinary per-document result.
/// </summary>
public sealed class DocumentCacheTargetMappingException : Exception
{
    public DocumentCacheTargetMappingException(
        DocumentCacheTargetMappingFailureReason reason,
        DocumentCacheMaterializerFailureMetadata metadata
    )
        : base(
            BuildMessage(
                DocumentCacheMaterializerGuards.RequireDefined(
                    reason,
                    nameof(reason),
                    "Unsupported target-mapping failure reason."
                ),
                metadata
            )
        )
    {
        Reason = reason;
        FailureMetadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public DocumentCacheTargetMappingFailureReason Reason { get; }

    public DocumentCacheMaterializerFailureMetadata FailureMetadata { get; }

    private static string BuildMessage(
        DocumentCacheTargetMappingFailureReason reason,
        DocumentCacheMaterializerFailureMetadata metadata
    )
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return $"DocumentCache target mapping failure '{reason}' for target "
            + $"'{DocumentCacheMaterializerDiagnosticFormatting.FormatTargetKey(metadata.TargetKey)}', mapping set "
            + $"'{DocumentCacheMaterializerDiagnosticFormatting.FormatMappingSetKey(metadata.MappingSetKey)}', "
            + $"document id {metadata.DocumentId}, resource key id "
            + $"{metadata.ResourceKeyId?.ToString() ?? "<unknown>"}.";
    }
}

internal static class DocumentCacheMaterializerDiagnosticFormatting
{
    public static string FormatTargetKey(DocumentCacheProjectionTargetKey key) =>
        $"{LogSanitizer.SanitizeForLog(key.TenantKey)}/{key.DataStoreId.Value}";

    public static string FormatMappingSetKey(MappingSetKey key) =>
        $"{LogSanitizer.SanitizeForLog(key.EffectiveSchemaHash)}/{key.Dialect}/{LogSanitizer.SanitizeForLog(key.RelationalMappingVersion)}";
}
