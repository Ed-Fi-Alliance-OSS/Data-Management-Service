// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Backend;

internal sealed record DocumentCacheWriterCurrentObservation(
    long? SourceContentVersion,
    long? CacheContentVersion,
    long? WorkRequiredContentVersion,
    Guid? SourceDocumentUuid,
    short? SourceResourceKeyId,
    string? SourceProjectName,
    string? SourceResourceName,
    string? SourceResourceVersion
)
{
    public DocumentCacheWriterCurrentStateObservation ToCurrentState() =>
        new(SourceContentVersion, CacheContentVersion, WorkRequiredContentVersion);
}

internal static class DocumentCacheWriterSupport
{
    public static DocumentCacheWriterCandidateObservation BuildCandidateObservation(
        DocumentCacheWriterRequest request,
        DocumentCacheWriterCurrentObservation currentObservation
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentObservation);

        DocumentCacheMaterializationCandidate? candidate = request.Candidate;
        if (candidate is null)
        {
            return DocumentCacheWriterCandidateObservation.Absent;
        }

        return new DocumentCacheWriterCandidateObservation(
            candidate,
            CompareCandidateMetadata(request.TargetContext.MappingSet, candidate, currentObservation)
        );
    }

    public static DocumentCacheWriterCandidateMetadataComparison CompareCandidateMetadata(
        MappingSet mappingSet,
        DocumentCacheMaterializationCandidate candidate,
        DocumentCacheWriterCurrentObservation currentObservation
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentObservation);

        if (currentObservation.SourceContentVersion is null)
        {
            return DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource;
        }

        if (currentObservation.SourceDocumentUuid != candidate.DocumentUuid.Value)
        {
            return DocumentCacheWriterCandidateMetadataComparison.DocumentUuidMismatch;
        }

        if (
            !string.Equals(
                currentObservation.SourceProjectName,
                candidate.ProjectName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                currentObservation.SourceResourceName,
                candidate.ResourceName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                currentObservation.SourceResourceVersion,
                candidate.ResourceVersion,
                StringComparison.Ordinal
            )
        )
        {
            return DocumentCacheWriterCandidateMetadataComparison.ResourceMetadataMismatch;
        }

        if (
            currentObservation.SourceResourceKeyId is null
            || !mappingSet.ResourceKeyById.TryGetValue(
                currentObservation.SourceResourceKeyId.Value,
                out ResourceKeyEntry? resourceKey
            )
            || resourceKey.ResourceKeyId != currentObservation.SourceResourceKeyId.Value
            || !string.Equals(
                resourceKey.Resource.ProjectName,
                candidate.ProjectName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                resourceKey.Resource.ResourceName,
                candidate.ResourceName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                resourceKey.ResourceVersion,
                candidate.ResourceVersion,
                StringComparison.Ordinal
            )
        )
        {
            return DocumentCacheWriterCandidateMetadataComparison.TargetMappingMismatch;
        }

        return DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource;
    }

    public static DocumentCacheWriterResult? SelectLifecycleFence(
        DocumentCacheWriterPurpose purpose,
        DocumentCacheLifecycleReadResult lifecycleReadResult
    )
    {
        DocumentCacheWriterClassificationSelection selection =
            DocumentCacheWriterClassificationSelector.Select(
                new DocumentCacheWriterClassificationRequest(
                    purpose,
                    lifecycleReadResult,
                    new DocumentCacheWriterCurrentStateObservation(
                        sourceContentVersion: null,
                        cacheContentVersion: null,
                        workRequiredContentVersion: null
                    ),
                    DocumentCacheWriterCandidateObservation.Absent
                )
            );

        return selection.Outcome == DocumentCacheWriterOutcome.LifecycleOrLatchFenced
            ? selection.TerminalResult
            : null;
    }

    public static async Task RollbackIfNeededAsync(
        DbTransaction transaction,
        bool transactionCompleted,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transactionCompleted)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            _ = exception;
        }
        catch (DbException exception)
        {
            _ = exception;
        }
    }

    public static long? GetNullableInt64(DbDataReader reader, string columnName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    public static short? GetNullableInt16(DbDataReader reader, string columnName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
    }

    public static Guid? GetNullableGuid(DbDataReader reader, string columnName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    public static string? GetNullableString(DbDataReader reader, string columnName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static void RecordTransactionDuration(
        IDocumentCacheWriterTelemetry telemetry,
        RelationalProviderToken providerToken,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome,
        long startTimestamp
    )
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        telemetry.RecordTransactionDuration(
            CreateMetricContext(providerToken, request, lifecycleState, outcome),
            DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp)
        );
    }

    public static void RecordCacheDmlDuration(
        IDocumentCacheWriterTelemetry telemetry,
        RelationalProviderToken providerToken,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome,
        long startTimestamp
    )
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        TimeSpan duration = DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp);
        DocumentCacheWriterMetricContext context = CreateMetricContext(
            providerToken,
            request,
            lifecycleState,
            outcome
        );
        telemetry.RecordCacheDmlDuration(context, duration);
        telemetry.RecordSameDocumentWait(
            context,
            DocumentCacheWriterContentionParticipant.CacheWriter,
            DocumentCacheWriterContentionPhase.CacheDml,
            duration
        );
    }

    public static void RecordAcknowledgementDuration(
        IDocumentCacheWriterTelemetry telemetry,
        RelationalProviderToken providerToken,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome,
        long startTimestamp
    )
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        TimeSpan duration = DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp);
        DocumentCacheWriterMetricContext context = CreateMetricContext(
            providerToken,
            request,
            lifecycleState,
            outcome
        );
        telemetry.RecordAcknowledgementDuration(context, duration);
        telemetry.RecordSameDocumentWait(
            context,
            DocumentCacheWriterContentionParticipant.CacheWriter,
            DocumentCacheWriterContentionPhase.Acknowledgement,
            duration
        );
    }

    private static DocumentCacheWriterMetricContext CreateMetricContext(
        RelationalProviderToken providerToken,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return DocumentCacheWriterMetricContext.ForCacheWriter(
            providerToken,
            request.TargetContext.TargetKey,
            request.Purpose,
            lifecycleState,
            outcome
        );
    }
}
