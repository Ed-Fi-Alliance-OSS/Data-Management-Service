// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache;

public sealed record DocumentCacheDownstreamPublicationHistoryObservation
{
    public DocumentCacheDownstreamPublicationHistoryObservation(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? physicalSourceFingerprint,
        DocumentCacheDownstreamPublicationStatus status,
        string? evidenceSource,
        string? evidenceGenerationIdentifier,
        DateTimeOffset observedAt,
        string diagnosticText
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);

        string? sanitizedEvidenceSource = SanitizeNullable(evidenceSource);
        string? sanitizedEvidenceGenerationIdentifier = SanitizeNullable(evidenceGenerationIdentifier);
        if (sanitizedEvidenceSource is null && sanitizedEvidenceGenerationIdentifier is null)
        {
            throw new ArgumentException(
                "Downstream publication history observations require an evidence source or generation identifier.",
                nameof(evidenceSource)
            );
        }

        TargetKey = targetKey;
        PhysicalSourceFingerprint = physicalSourceFingerprint;
        Status = status;
        EvidenceSource = sanitizedEvidenceSource;
        EvidenceGenerationIdentifier = sanitizedEvidenceGenerationIdentifier;
        ObservedAt = observedAt;
        DiagnosticText = DocumentCacheDiagnosticText.Sanitize(diagnosticText);
    }

    public DocumentCacheTargetKey TargetKey { get; }

    public DocumentCachePhysicalSourceFingerprint? PhysicalSourceFingerprint { get; }

    public DocumentCacheDownstreamPublicationStatus Status { get; }

    public string? EvidenceSource { get; }

    public string? EvidenceGenerationIdentifier { get; }

    public DateTimeOffset ObservedAt { get; }

    public string DiagnosticText { get; }

    private static string? SanitizeNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string sanitized = DocumentCacheDiagnosticText.Sanitize(value);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }
}

public interface IDocumentCacheDownstreamPublicationHistoryProvider
{
    Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        CancellationToken cancellationToken = default
    );
}

public sealed class DocumentCacheUnknownDownstreamPublicationHistoryProvider(TimeProvider timeProvider)
    : IDocumentCacheDownstreamPublicationHistoryProvider
{
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Task<DocumentCacheDownstreamPublicationHistoryObservation> ObserveAsync(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentCacheDownstreamPublicationHistoryObservation observation = new(
            targetKey,
            currentPhysicalSourceFingerprint,
            DocumentCacheDownstreamPublicationStatus.Unknown,
            evidenceSource: "document-cache-default-downstream-publication-history",
            evidenceGenerationIdentifier: null,
            _timeProvider.GetUtcNow(),
            "Downstream publication history is unknown until durable CDC binding state is available."
        );

        return Task.FromResult(observation);
    }
}

public sealed record DocumentCacheDownstreamPublicationHistoryProofResult
{
    private DocumentCacheDownstreamPublicationHistoryProofResult(
        bool isAccepted,
        DocumentCacheAdministrativePreflightClassification classification,
        DocumentCacheDownstreamPublicationHistoryObservation observation,
        ImmutableArray<DocumentCacheAdministrativeDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (isAccepted && classification != DocumentCacheAdministrativePreflightClassification.Eligible)
        {
            throw new ArgumentException(
                "Accepted downstream publication proof must use the Eligible classification.",
                nameof(classification)
            );
        }

        if (!isAccepted && classification == DocumentCacheAdministrativePreflightClassification.Eligible)
        {
            throw new ArgumentException(
                "Rejected downstream publication proof must not use the Eligible classification.",
                nameof(classification)
            );
        }

        if (isAccepted && !diagnostics.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Accepted downstream publication proof must not carry diagnostics.",
                nameof(diagnostics)
            );
        }

        if (!isAccepted && diagnostics.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Rejected downstream publication proof requires diagnostics.",
                nameof(diagnostics)
            );
        }

        IsAccepted = isAccepted;
        Classification = classification;
        Observation = observation;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    public bool IsAccepted { get; }

    public DocumentCacheAdministrativePreflightClassification Classification { get; }

    public DocumentCacheDownstreamPublicationHistoryObservation Observation { get; }

    public DocumentCacheDownstreamPublicationStatus DownstreamPublicationStatus => Observation.Status;

    public ImmutableArray<DocumentCacheAdministrativeDiagnostic> Diagnostics { get; }

    public static DocumentCacheDownstreamPublicationHistoryProofResult Accepted(
        DocumentCacheDownstreamPublicationHistoryObservation observation
    ) =>
        new(
            isAccepted: true,
            DocumentCacheAdministrativePreflightClassification.Eligible,
            observation,
            diagnostics: []
        );

    public static DocumentCacheDownstreamPublicationHistoryProofResult Rejected(
        DocumentCacheAdministrativePreflightClassification classification,
        DocumentCacheDownstreamPublicationHistoryObservation observation,
        DocumentCacheAdministrativeDiagnostic diagnostic
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new(isAccepted: false, classification, observation, [diagnostic]);
    }
}

public static class DocumentCacheDownstreamPublicationHistoryProofEvaluator
{
    public static DocumentCacheDownstreamPublicationHistoryProofResult Evaluate(
        DocumentCacheTargetKey targetKey,
        DocumentCachePhysicalSourceFingerprint? currentPhysicalSourceFingerprint,
        DocumentCacheDownstreamPublicationHistoryObservation observation,
        DocumentCachePhysicalSourceFingerprint? expectedPhysicalSourceFingerprint = null
    )
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentNullException.ThrowIfNull(observation);

        if (!targetKey.Equals(observation.TargetKey))
        {
            return RejectedForSourceMismatch(
                observation,
                "Downstream publication history observation is bound to a different target."
            );
        }

        if (currentPhysicalSourceFingerprint is null)
        {
            return RejectedForSourceMismatch(
                observation,
                "Current resolved physical-source fingerprint is required for downstream publication proof."
            );
        }

        if (
            expectedPhysicalSourceFingerprint is not null
            && !currentPhysicalSourceFingerprint.Equals(expectedPhysicalSourceFingerprint)
        )
        {
            return RejectedForSourceMismatch(
                observation,
                "Expected physical-source fingerprint does not match the current target observation."
            );
        }

        if (observation.PhysicalSourceFingerprint is null)
        {
            return RejectedForSourceMismatch(
                observation,
                "Downstream publication history observation did not include a physical-source fingerprint."
            );
        }

        if (!observation.PhysicalSourceFingerprint.Equals(currentPhysicalSourceFingerprint))
        {
            return RejectedForSourceMismatch(
                observation,
                "Downstream publication history observation does not match the current physical source."
            );
        }

        if (
            expectedPhysicalSourceFingerprint is not null
            && !observation.PhysicalSourceFingerprint.Equals(expectedPhysicalSourceFingerprint)
        )
        {
            return RejectedForSourceMismatch(
                observation,
                "Downstream publication history observation does not match the expected physical source."
            );
        }

        if (observation.Status != DocumentCacheDownstreamPublicationStatus.InternalOnly)
        {
            return DocumentCacheDownstreamPublicationHistoryProofResult.Rejected(
                DocumentCacheAdministrativePreflightClassification.DownstreamHistoryPresentOrUnknown,
                observation,
                new DocumentCacheAdministrativeDiagnostic(
                    DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
                    "Downstream publication history is not internal-only."
                )
            );
        }

        return DocumentCacheDownstreamPublicationHistoryProofResult.Accepted(observation);
    }

    private static DocumentCacheDownstreamPublicationHistoryProofResult RejectedForSourceMismatch(
        DocumentCacheDownstreamPublicationHistoryObservation observation,
        string message
    ) =>
        DocumentCacheDownstreamPublicationHistoryProofResult.Rejected(
            DocumentCacheAdministrativePreflightClassification.ExpectedSourceMismatch,
            observation,
            new DocumentCacheAdministrativeDiagnostic(
                DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch,
                message
            )
        );
}
