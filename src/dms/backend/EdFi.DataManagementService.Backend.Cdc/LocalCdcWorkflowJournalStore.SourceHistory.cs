// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class LocalCdcWorkflowJournalStore
{
    // Stopping consumption and recording cleanup intent must remain available even when publication
    // evidence is missing. Projection-only work is not downstream exposure. Resuming consumption is.
    private static bool CanExposeSource(CdcWorkflowEffect effect) =>
        effect
            is CdcWorkflowEffect.ReserveBinding
                or CdcWorkflowEffect.CreateProvider
                or CdcWorkflowEffect.PrepareKafka
                or CdcWorkflowEffect.RegisterConnector
                or CdcWorkflowEffect.EstablishConnector
                or CdcWorkflowEffect.ResumeConnector
                or CdcWorkflowEffect.AuthorizeWriterPublication
                or CdcWorkflowEffect.IncreaseRecordSize;

    public sealed partial class Session
    {
        /// <summary>
        /// Reads trusted history for the normalized target and current source. Instance/generation
        /// changes cannot hide earlier exposure. Missing or contradictory provenance throws; callers
        /// must map it to rejecting/unknown evidence, never internal-only eligibility.
        /// </summary>
        public Task<CdcSourcePublicationHistory> ReadSourcePublicationHistoryAsync(
            CdcTargetIdentity target,
            string physicalSourceFingerprint,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                () => _store.ReadSourceHistoryAsync(target, physicalSourceFingerprint, cancellationToken),
                cancellationToken
            );

        /// <summary>
        /// Call before any managed downstream exposure outside the journal's binding/connector
        /// workflow, while holding this session through the external effect. A failed effect never
        /// reverses the transition. This method cannot retrospectively attest an unmanaged source.
        /// </summary>
        public Task<CdcSourcePublicationHistory> RecordSourceExposureAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            string physicalSourceFingerprint,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    CdcWorkflowJournal journal = await ReadOwnedAsync(target, workflowId, cancellationToken);
                    Require(
                        SourceFingerprint(journal) == physicalSourceFingerprint,
                        CdcWorkflowStateFailure.Contradictory
                    );
                    return await _store.AdvanceSourceHistoryAsync(
                        journal,
                        DocumentCacheDownstreamPublicationStatus.Possible,
                        cancellationToken
                    );
                },
                cancellationToken
            );
    }

    private static CdcDatabaseCreationReceipt CreationReceipt(CdcWorkflowJournal journal)
    {
        var receipts = journal
            .Operations.SelectMany(o => o.Completions)
            .Select(c => c.Evidence)
            .OfType<CdcWorkflowCompletion.Database>()
            .ToArray();
        Require(receipts.Length == 1, CdcWorkflowStateFailure.Contradictory);
        return receipts[0].Receipt;
    }

    private static string SourceFingerprint(CdcWorkflowJournal journal)
    {
        var sources = journal
            .Operations.SelectMany(o => o.Completions)
            .Select(c => c.Evidence)
            .OfType<CdcWorkflowCompletion.Source>()
            .ToArray();
        Require(sources.Length == 1, CdcWorkflowStateFailure.Contradictory);
        return sources[0].PhysicalSourceFingerprint;
    }

    private async Task CreateSourceHistoryAsync(
        CdcWorkflowJournal journal,
        CancellationToken cancellationToken
    )
    {
        Require(
            journal.Operations.All(o =>
                o.Effect is CdcWorkflowEffect.CreateDatabase or CdcWorkflowEffect.AssociateSource
            ),
            CdcWorkflowStateFailure.Contradictory
        );
        CdcSourcePublicationHistory history = new(
            CdcSourcePublicationHistory.CurrentVersion,
            journal.WorkflowId,
            journal.Target,
            CreationReceipt(journal),
            SourceFingerprint(journal),
            [new(DocumentCacheDownstreamPublicationStatus.InternalOnly, Now())]
        );
        ValidateSourceHistory(history);
        await WritePayloadAsync(
            SourceHistoryPath(journal.Target, history.PhysicalSourceFingerprint, createDirectories: true),
            JsonSerializer.Serialize(history, _json),
            create: true,
            cancellationToken
        );
    }

    private async Task<CdcSourcePublicationHistory> ReadSourceHistoryAsync(
        CdcTargetIdentity target,
        string fingerprint,
        CancellationToken cancellationToken
    )
    {
        string path = SourceHistoryPath(target, fingerprint, createDirectories: false);
        CdcSourcePublicationHistory history;
        try
        {
            string payload = await File.ReadAllTextAsync(path, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(payload);
            ValidateUniqueProperties(document.RootElement);
            Require(
                document.RootElement.GetProperty("version").GetInt32()
                    == CdcSourcePublicationHistory.CurrentVersion,
                CdcWorkflowStateFailure.UnsupportedVersion
            );
            history =
                JsonSerializer.Deserialize<CdcSourcePublicationHistory>(payload, _json)
                ?? throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
            ValidateSourceHistory(history);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Missing);
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or ArgumentException
                        or InvalidOperationException
                        or KeyNotFoundException
            )
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
        }

        Require(
            history.PhysicalSourceFingerprint == fingerprint
                && history.CreationTarget.DeploymentKey == target.DeploymentKey
                && history.CreationTarget.TenantKey == target.TenantKey
                && history.CreationTarget.DataStoreId == target.DataStoreId
                && history.CreationTarget.Provider == target.Provider,
            CdcWorkflowStateFailure.Contradictory
        );
        CdcWorkflowJournal journal = await ReadAsync(history.CreationTarget, cancellationToken);
        Require(
            journal.WorkflowId == history.WorkflowId
                && CreationReceipt(journal) == history.CreationReceipt
                && SourceFingerprint(journal) == fingerprint,
            CdcWorkflowStateFailure.Contradictory
        );
        var sourceCompletion = journal
            .Operations.Single(o => o.Effect == CdcWorkflowEffect.AssociateSource)
            .Completions.Single();
        Require(
            history.Transitions[0].RecordedAt >= sourceCompletion.ReconciledAt,
            CdcWorkflowStateFailure.Contradictory
        );
        if (history.Transitions[^1].Status == DocumentCacheDownstreamPublicationStatus.InternalOnly)
        {
            Require(
                !journal.Operations.Any(o => CanExposeSource(o.Effect)),
                CdcWorkflowStateFailure.Contradictory
            );
        }
        return history;
    }

    private async Task<CdcSourcePublicationHistory> AdvanceSourceHistoryAsync(
        CdcWorkflowJournal journal,
        DocumentCacheDownstreamPublicationStatus requested,
        CancellationToken cancellationToken
    )
    {
        string fingerprint = SourceFingerprint(journal);
        CdcSourcePublicationHistory history = await ReadSourceHistoryAsync(
            journal.Target,
            fingerprint,
            cancellationToken
        );
        Require(
            history.WorkflowId == journal.WorkflowId && history.CreationTarget == journal.Target,
            CdcWorkflowStateFailure.Contradictory
        );
        if (ExposureRank(requested) <= ExposureRank(history.Transitions[^1].Status))
        {
            return history;
        }
        CdcSourcePublicationHistory next = history with
        {
            Transitions = history.Transitions.Add(new(requested, Now())),
        };
        ValidateSourceHistory(next);
        await WritePayloadAsync(
            SourceHistoryPath(journal.Target, fingerprint, createDirectories: false),
            JsonSerializer.Serialize(next, _json),
            create: false,
            cancellationToken
        );
        return next;
    }

    private void ValidateSourceHistory(CdcSourcePublicationHistory history)
    {
        Require(
            history.Version == CdcSourcePublicationHistory.CurrentVersion,
            CdcWorkflowStateFailure.UnsupportedVersion
        );
        Require(
            history.WorkflowId != Guid.Empty
                && history.CreationTarget is not null
                && history.CreationReceipt is not null
        );
        Require(
            CdcTargetValidator
                .ValidateBindingIdentity(CdcBindingIdentity.FromTargetIdentity(history.CreationTarget!))
                .Succeeded && Enum.IsDefined(history.CreationTarget!.Provider)
        );
        Require(
            history.CreationReceipt!.ReceiptId != Guid.Empty
                && history.CreationReceipt.Outcome == CdcDatabaseCreationOutcome.Created
        );
        Require(CdcSha256ValueValidator.IsValid(history.PhysicalSourceFingerprint));
        Require(!history.Transitions.IsDefaultOrEmpty && history.Transitions.Length <= 4);
        DateTimeOffset previous = default;
        int rank = -1;
        foreach (CdcSourcePublicationTransition transition in history.Transitions)
        {
            Require(transition is not null);
            int nextRank = ExposureRank(transition!.Status);
            Require(nextRank > rank && (rank != -1 || nextRank == 0));
            Require(
                transition.RecordedAt != default
                    && transition.RecordedAt >= previous
                    && transition.RecordedAt <= Now()
                    && transition.RecordedAt.Offset == TimeSpan.Zero
            );
            rank = nextRank;
            previous = transition.RecordedAt;
        }
    }

    private static int ExposureRank(DocumentCacheDownstreamPublicationStatus status) =>
        status switch
        {
            DocumentCacheDownstreamPublicationStatus.InternalOnly => 0,
            DocumentCacheDownstreamPublicationStatus.Possible => 1,
            DocumentCacheDownstreamPublicationStatus.Active => 2,
            DocumentCacheDownstreamPublicationStatus.Historical => 3,
            _ => throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid),
        };

    private string SourceHistoryPath(CdcTargetIdentity target, string fingerprint, bool createDirectories)
    {
        Require(target is not null && Enum.IsDefined(target.Provider));
        CdcStateStorePathResolution resolved = _paths.ResolveBindingPath(
            CdcBindingIdentity.FromTargetIdentity(target!)
        );
        Require(resolved.Succeeded && CdcSha256ValueValidator.IsValid(fingerprint));
        // Use the existing opaque fingerprint, never raw source identifiers or another fingerprint algorithm.
        // No instance/generation path component: surviving source history outlives binding cleanup.
        string path = _paths.RootPath;
        foreach (string segment in new[] { "source-history", resolved.DeploymentKey! })
        {
            if (createDirectories)
            {
                EnsureDirectory(path);
            }
            else
            {
                ValidateEntry(path, file: false);
            }
            path = Path.Combine(path, segment);
        }
        if (createDirectories)
        {
            EnsureDirectory(path);
        }
        else
        {
            ValidateEntry(path, file: false);
        }
        path = Path.Combine(path, fingerprint["sha256:".Length..] + ".json");
        ValidateEntry(path, file: true);
        return path;
    }
}
