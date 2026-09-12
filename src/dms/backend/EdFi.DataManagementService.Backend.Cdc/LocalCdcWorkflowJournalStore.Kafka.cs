// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Historical Kafka effects, never readiness. Shared scopes depend on deployment/worker only and
/// survive binding retirement. Only hashes of configuration/ACL intent are persisted.
/// </summary>
internal sealed record CdcKafkaPreparationJournal(
    int Version,
    string ScopeHash,
    string TopicIntentHash,
    int TopicCount,
    ImmutableArray<int> ReconciledTopics,
    string GrantIntentHash,
    bool GrantsReconciled
);

public sealed partial class LocalCdcWorkflowJournalStore
{
    public sealed partial class Session
    {
        internal Task<CdcKafkaPreparationJournal> ReadKafkaAsync(string scopeHash, CancellationToken token) =>
            RunAsync(() => _store.ReadKafkaAsync(scopeHash, token), token);

        internal Task<CdcKafkaPreparationJournal> WriteKafkaAsync(
            CdcKafkaPreparationJournal journal,
            bool create,
            CancellationToken token
        ) =>
            RunAsync(
                async () =>
                {
                    ValidateKafka(journal);
                    await _store.WritePayloadAsync(
                        _store.KafkaPath(journal.ScopeHash, true),
                        JsonSerializer.Serialize(journal, _json),
                        create,
                        token
                    );
                    return journal;
                },
                token
            );
    }

    private async Task<CdcKafkaPreparationJournal> ReadKafkaAsync(string scopeHash, CancellationToken token)
    {
        try
        {
            string payload = await File.ReadAllTextAsync(KafkaPath(scopeHash, false), token);
            using var document = JsonDocument.Parse(payload);
            ValidateUniqueProperties(document.RootElement);
            var journal =
                JsonSerializer.Deserialize<CdcKafkaPreparationJournal>(payload, _json)
                ?? throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
            ValidateKafka(journal);
            Require(journal.ScopeHash == scopeHash, CdcWorkflowStateFailure.Contradictory);
            return journal;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Missing);
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
        }
    }

    private string KafkaPath(string scopeHash, bool create)
    {
        Require(CdcSha256ValueValidator.IsValid(scopeHash));
        string directory = Path.Combine(_paths.RootPath, "kafka-preparation");
        ValidateEntry(_paths.RootPath, file: false);
        if (create)
        {
            EnsureDirectory(directory);
        }
        else
        {
            ValidateEntry(directory, file: false);
        }
        string path = Path.Combine(directory, scopeHash[7..] + ".json");
        ValidateEntry(path, file: true);
        return path;
    }

    private static void ValidateKafka(CdcKafkaPreparationJournal journal)
    {
        Require(journal.Version == 1, CdcWorkflowStateFailure.UnsupportedVersion);
        Require(CdcSha256ValueValidator.IsValid(journal.ScopeHash));
        Require(CdcSha256ValueValidator.IsValid(journal.TopicIntentHash));
        Require(CdcSha256ValueValidator.IsValid(journal.GrantIntentHash));
        Require(journal.TopicCount is >= 1 and <= 3 && !journal.ReconciledTopics.IsDefault);
        Require(journal.ReconciledTopics.All(i => i >= 0 && i < journal.TopicCount));
        Require(journal.ReconciledTopics.Distinct().Count() == journal.ReconciledTopics.Length);
        Require(!journal.GrantsReconciled || journal.ReconciledTopics.Length == journal.TopicCount);
    }
}
