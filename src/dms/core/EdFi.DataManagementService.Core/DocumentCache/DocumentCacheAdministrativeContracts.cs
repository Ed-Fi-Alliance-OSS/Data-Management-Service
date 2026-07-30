// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache;

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeCommand>))]
public enum DocumentCacheAdministrativeCommand
{
    GuardedNewEmptyActivation,
    OfflineReadAccelerationActivation,
    OfflineDeactivation,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativePreflightClassification>))]
public enum DocumentCacheAdministrativePreflightClassification
{
    Eligible,
    TargetNotConfigured,
    TargetUnresolved,
    TargetReplacedBeforeExecution,
    MissingOrInvalidInventory,
    ProviderPrerequisiteFailed,
    UnsupportedPrerequisiteIncident,
    LifecycleMismatch,
    ResettingRequiresExplicitOperatorRecovery,
    CacheAheadLatchSet,
    NonemptyGuardedActivationState,
    DownstreamHistoryPresentOrUnknown,
    ExpectedSourceMismatch,
    UnexpectedProviderFailure,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheDownstreamPublicationStatus>))]
public enum DocumentCacheDownstreamPublicationStatus
{
    InternalOnly,
    Active,
    Historical,
    Possible,
    Unknown,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheAdministrativeNoMutationScope>))]
public enum DocumentCacheAdministrativeNoMutationScope
{
    LifecycleCacheWorkLatchAndProviderSettings,
}

public sealed record DocumentCacheAdministrativeTargetKey
{
    [JsonConstructor]
    public DocumentCacheAdministrativeTargetKey(string? tenantKey, long dataStoreId)
    {
        TargetKey = DocumentCacheTargetKey.Create(tenantKey, dataStoreId);
    }

    [JsonIgnore]
    public DocumentCacheTargetKey TargetKey { get; }

    [JsonPropertyName("tenantKey")]
    public string TenantKey => TargetKey.TenantKey;

    [JsonPropertyName("dataStoreId")]
    public long DataStoreId => TargetKey.DataStoreId;

    public static DocumentCacheAdministrativeTargetKey FromTargetKey(DocumentCacheTargetKey targetKey)
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        return new(targetKey.TenantKey, targetKey.DataStoreId);
    }
}

public sealed record DocumentCacheGuardedNewEmptyActivationRequest
{
    [JsonConstructor]
    public DocumentCacheGuardedNewEmptyActivationRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }
}

public sealed record DocumentCacheOfflineReadAccelerationActivationRequest
{
    [JsonConstructor]
    public DocumentCacheOfflineReadAccelerationActivationRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }
}

public sealed record DocumentCacheOfflineDeactivationRequest
{
    [JsonConstructor]
    public DocumentCacheOfflineDeactivationRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }
}

public sealed record DocumentCacheAdministrativeCommandResult
{
    [JsonConstructor]
    public DocumentCacheAdministrativeCommandResult(
        DocumentCacheAdministrativeCommand command,
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheAdministrativePreflightClassification classification,
        DocumentCacheLifecycleState? observedLifecycle = null,
        bool? cacheAheadRecoveryRequired = null,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint = null,
        long? targetContextGeneration = null,
        DocumentCacheDownstreamPublicationStatus? downstreamPublicationStatus = null,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics = default,
        DocumentCacheAdministrativeNoMutationGuarantee? noMutationGuarantee = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        if (targetContextGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetContextGeneration),
                "Target-context generation must be positive when supplied."
            );
        }

        Command = command;
        TargetKey = targetKey;
        Classification = classification;
        ObservedLifecycle = observedLifecycle;
        CacheAheadRecoveryRequired = cacheAheadRecoveryRequired;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        TargetContextGeneration = targetContextGeneration;
        DownstreamPublicationStatus = downstreamPublicationStatus;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
        NoMutationGuarantee = noMutationGuarantee;
    }

    [JsonPropertyName("command")]
    public DocumentCacheAdministrativeCommand Command { get; }

    [JsonPropertyName("targetKey")]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("classification")]
    public DocumentCacheAdministrativePreflightClassification Classification { get; }

    [JsonPropertyName("observedLifecycle")]
    [JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheLifecycleState>))]
    public DocumentCacheLifecycleState? ObservedLifecycle { get; }

    [JsonPropertyName("cacheAheadRecoveryRequired")]
    public bool? CacheAheadRecoveryRequired { get; }

    [JsonPropertyName("physicalSourceFingerprint")]
    [JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    [JsonPropertyName("targetContextGeneration")]
    public long? TargetContextGeneration { get; }

    [JsonPropertyName("downstreamPublicationStatus")]
    public DocumentCacheDownstreamPublicationStatus? DownstreamPublicationStatus { get; }

    [JsonPropertyName("diagnostics")]
    public ImmutableArray<DocumentCacheAdministrativeDiagnostic> Diagnostics { get; }

    [JsonPropertyName("noMutationGuarantee")]
    public DocumentCacheAdministrativeNoMutationGuarantee? NoMutationGuarantee { get; }
}

public sealed record DocumentCacheAdministrativeDiagnostic
{
    [JsonConstructor]
    public DocumentCacheAdministrativeDiagnostic(
        DocumentCacheTargetDiagnosticCategory category,
        string message
    )
    {
        Category = category;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    [JsonPropertyName("category")]
    [JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheTargetDiagnosticCategory>))]
    public DocumentCacheTargetDiagnosticCategory Category { get; }

    [JsonPropertyName("message")]
    public string Message { get; }
}

public sealed record DocumentCacheAdministrativeNoMutationGuarantee
{
    [JsonConstructor]
    public DocumentCacheAdministrativeNoMutationGuarantee(
        bool guaranteed,
        DocumentCacheAdministrativeNoMutationScope scope,
        string message
    )
    {
        Guaranteed = guaranteed;
        Scope = scope;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    [JsonPropertyName("guaranteed")]
    public bool Guaranteed { get; }

    [JsonPropertyName("scope")]
    public DocumentCacheAdministrativeNoMutationScope Scope { get; }

    [JsonPropertyName("message")]
    public string Message { get; }
}

public sealed class DocumentCachePhysicalSourceFingerprintJsonConverter
    : JsonConverter<DocumentCachePhysicalSourceFingerprint>
{
    public override DocumentCachePhysicalSourceFingerprint Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Physical-source fingerprint must be a string.");
        }

        string? value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("Physical-source fingerprint must be a string.");
        }

        try
        {
            return new DocumentCachePhysicalSourceFingerprint(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                "Physical-source fingerprint must be `sha256:` followed by 64 lowercase hexadecimal characters.",
                exception
            );
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        DocumentCachePhysicalSourceFingerprint value,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(value.Value);
    }
}

public sealed class LowerCamelJsonStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public LowerCamelJsonStringEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}
