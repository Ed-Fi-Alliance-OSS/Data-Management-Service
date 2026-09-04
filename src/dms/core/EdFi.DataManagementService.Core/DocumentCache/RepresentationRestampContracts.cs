// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.DocumentCache;

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheRepresentationRestampMode>))]
public enum DocumentCacheRepresentationRestampMode
{
    Tracking,
    Disabled,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheRepresentationRestampOperationState>))]
public enum DocumentCacheRepresentationRestampOperationState
{
    Draft,
    Incomplete,
    Completed,
}

[JsonConverter(typeof(LowerCamelJsonStringEnumConverter<DocumentCacheRepresentationRestampClaimLevel>))]
public enum DocumentCacheRepresentationRestampClaimLevel
{
    CanonicalOnlyComplete,
    ProjectionWorkQueued,
    Incomplete,
}

#pragma warning disable S1694
[JsonPolymorphic(TypeDiscriminatorPropertyName = "scopeType")]
[JsonDerivedType(typeof(DocumentCacheRepresentationRestampResourceScope), "resource")]
[JsonDerivedType(typeof(DocumentCacheRepresentationRestampDocumentUuidsScope), "documentUuids")]
public abstract record DocumentCacheRepresentationRestampScope
{
    private protected DocumentCacheRepresentationRestampScope() { }

    public abstract bool TryCanonicalize(
        int projectorPageSize,
        out DocumentCacheRepresentationRestampScope canonicalScope
    );
}
#pragma warning restore S1694

public sealed record DocumentCacheRepresentationRestampResourceScope(
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("resourceName")] string ResourceName
) : DocumentCacheRepresentationRestampScope
{
    public override bool TryCanonicalize(
        int projectorPageSize,
        out DocumentCacheRepresentationRestampScope canonicalScope
    )
    {
        canonicalScope = this;
        if (
            projectorPageSize <= 0
            || string.IsNullOrWhiteSpace(ProjectName)
            || string.IsNullOrWhiteSpace(ResourceName)
        )
        {
            return false;
        }

        canonicalScope = new DocumentCacheRepresentationRestampResourceScope(
            ProjectName.Trim(),
            ResourceName.Trim()
        );
        return true;
    }
}

public sealed record DocumentCacheRepresentationRestampDocumentUuidsScope(
    [property: JsonPropertyName("documentUuids")] ImmutableArray<Guid> DocumentUuids
) : DocumentCacheRepresentationRestampScope
{
    public override bool TryCanonicalize(
        int projectorPageSize,
        out DocumentCacheRepresentationRestampScope canonicalScope
    )
    {
        canonicalScope = this;
        if (
            projectorPageSize <= 0
            || DocumentUuids.IsDefaultOrEmpty
            || DocumentUuids.Length > projectorPageSize
            || DocumentUuids.Any(uuid => uuid == Guid.Empty)
            || DocumentUuids.Distinct().Count() != DocumentUuids.Length
        )
        {
            return false;
        }

        canonicalScope = new DocumentCacheRepresentationRestampDocumentUuidsScope([
            .. DocumentUuids.OrderBy(uuid => uuid.ToString("D"), StringComparer.Ordinal),
        ]);
        return true;
    }
}

public sealed record DocumentCacheRepresentationRestampPreviewRequest
    : IDocumentCacheOfflineWriterAdmissionRequest
{
    [JsonConstructor]
    public DocumentCacheRepresentationRestampPreviewRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCacheRepresentationRestampMode mode,
        string reason,
        DocumentCacheRepresentationRestampScope scope,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1024)
        {
            throw new ArgumentException(
                "Reason must be nonblank and at most 1024 characters.",
                nameof(reason)
            );
        }
        TargetKey = targetKey;
        OfflineWriterAdmission = offlineWriterAdmission?.WithCommandSpecificConfirmation(
            DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
        );
        Mode = mode;
        Reason = reason;
        Scope = scope;
        ExpectedPhysicalSourceFingerprint = expectedPhysicalSourceFingerprint;
    }

    [JsonPropertyName("targetKey")]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("offlineWriterAdmission")]
    public DocumentCacheOfflineWriterAdmission? OfflineWriterAdmission { get; }

    [JsonPropertyName("mode")]
    public DocumentCacheRepresentationRestampMode Mode { get; }

    [JsonPropertyName("reason")]
    public string Reason { get; }

    [JsonPropertyName("scope")]
    public DocumentCacheRepresentationRestampScope Scope { get; }

    [JsonPropertyName("expectedPhysicalSourceFingerprint")]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint { get; }

    [JsonIgnore]
    public DocumentCacheAdministrativeCommandConfirmation? Confirmation => null;
}

public sealed record DocumentCacheRepresentationRestampExecuteRequest
    : IDocumentCacheOfflineWriterAdmissionRequest
{
    [JsonConstructor]
    public DocumentCacheRepresentationRestampExecuteRequest(
        DocumentCacheAdministrativeTargetKey targetKey,
        Guid operationId,
        DocumentCacheOfflineWriterAdmission? offlineWriterAdmission,
        DocumentCacheAdministrativeCommandConfirmation? confirmation = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        TargetKey = targetKey;
        OperationId = operationId;
        OfflineWriterAdmission = offlineWriterAdmission?.WithCommandSpecificConfirmation(
            DocumentCacheOfflineWriterAdmissionConfirmation.RepresentationRestampWritersClosedAndDrained
        );
        Confirmation = confirmation;
    }

    [JsonPropertyName("targetKey")]
    public DocumentCacheAdministrativeTargetKey TargetKey { get; }

    [JsonPropertyName("operationId")]
    public Guid OperationId { get; }

    [JsonPropertyName("offlineWriterAdmission")]
    public DocumentCacheOfflineWriterAdmission? OfflineWriterAdmission { get; }

    [JsonPropertyName("confirmation")]
    public DocumentCacheAdministrativeCommandConfirmation? Confirmation { get; }

    [JsonIgnore]
    public DocumentCachePhysicalSourceFingerprint? ExpectedPhysicalSourceFingerprint => null;
}

public sealed record DocumentCacheRepresentationRestampOperation(
    Guid OperationId,
    int ContractVersion,
    DocumentCacheAdministrativeTargetKey TargetKey,
    DocumentCachePhysicalSourceFingerprint PhysicalSourceFingerprint,
    DocumentCacheRepresentationRestampScope Scope,
    string Reason,
    DocumentCacheRepresentationRestampMode Mode,
    long PreRestampBoundary,
    long PreviewDocumentCount,
    long CommittedDocumentCount,
    DocumentCacheRepresentationRestampOperationState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record DocumentCacheRepresentationRestampResult(
    [property: JsonPropertyName("operationId"), JsonPropertyOrder(1)] Guid OperationId,
    [property: JsonPropertyName("state"), JsonPropertyOrder(2)]
        DocumentCacheRepresentationRestampOperationState State,
    [property: JsonPropertyName("preRestampBoundary"), JsonPropertyOrder(3)] long PreRestampBoundary,
    [property: JsonPropertyName("previewDocumentCount"), JsonPropertyOrder(4)] long PreviewDocumentCount,
    [property: JsonPropertyName("committedDocumentCount"), JsonPropertyOrder(5)] long CommittedDocumentCount,
    [property: JsonPropertyName("remainingEligibleDocumentCount"), JsonPropertyOrder(6)]
        long? RemainingEligibleDocumentCount,
    [property: JsonPropertyName("mode"), JsonPropertyOrder(7)] DocumentCacheRepresentationRestampMode Mode,
    [property: JsonPropertyName("physicalSourceFingerprint"), JsonPropertyOrder(8)]
    [property: JsonConverter(typeof(DocumentCachePhysicalSourceFingerprintJsonConverter))]
        DocumentCachePhysicalSourceFingerprint PhysicalSourceFingerprint,
    [property: JsonPropertyName("claimLevel"), JsonPropertyOrder(9)]
        DocumentCacheRepresentationRestampClaimLevel ClaimLevel
);
