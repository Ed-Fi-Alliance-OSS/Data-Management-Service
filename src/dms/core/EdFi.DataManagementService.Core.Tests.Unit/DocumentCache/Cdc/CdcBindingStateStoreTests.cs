// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcBindingStateStore")]
public class Given_CdcBindingStateStore
{
    private static readonly DateTimeOffset SampleObservedAt = new(2026, 8, 17, 13, 10, 11, TimeSpan.Zero);

    private static CdcBinding SampleBinding =>
        new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            CdcProvider.Postgresql,
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            "dms-local-data-store-1-g1",
            "edfi.dms.instance.data-store-1-g1.documents.v1",
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );

    [Test]
    public async Task It_creates_absent_bindings_and_accepts_later_exact_matches_without_rewriting()
    {
        InMemoryCdcBindingStateStore store = new();

        CdcCreateBindingStateStoreResult created = await store.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcCreateBindingStateStoreResult existingExactMatch = await store.CreateBindingIfAbsentAsync(
            SampleBinding,
            CancellationToken.None
        );
        CdcBinding changedBinding = SampleBinding with
        {
            PhysicalSourceFingerprint =
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        };
        CdcCreateBindingStateStoreResult mismatch = await store.CreateBindingIfAbsentAsync(
            changedBinding,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readBack = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        created.Should().BeOfType<CdcCreateBindingStateStoreResult.Created>();
        existingExactMatch.Should().BeOfType<CdcCreateBindingStateStoreResult.ExistingExactMatch>();
        CdcCreateBindingStateStoreResult.BindingMismatch mismatchResult = mismatch
            .Should()
            .BeOfType<CdcCreateBindingStateStoreResult.BindingMismatch>()
            .Subject;
        mismatchResult
            .Mismatch.Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.DifferentValue
                && difference.FieldName == "physicalSourceFingerprint"
            );
        readBack
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.Found>()
            .Subject.State.Binding.Should()
            .Be(SampleBinding);
    }

    [Test]
    public async Task It_distinguishes_valid_missing_mismatch_incident_latch_and_store_failure_results()
    {
        InMemoryCdcBindingStateStore store = new();
        CdcIncident incident = CreateIncident(SampleBinding);

        CdcReadBindingStateStoreResult missing = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );
        await store.CreateBindingIfAbsentAsync(SampleBinding, CancellationToken.None);
        CdcLatchIncidentStateStoreResult latched = await store.LatchSourceHistoryLossAsync(
            incident,
            CancellationToken.None
        );
        CdcLatchIncidentStateStoreResult alreadyLatched = await store.LatchSourceHistoryLossAsync(
            incident,
            CancellationToken.None
        );
        CdcExactMatchBindingStateStoreResult mismatch = await store.ExactMatchBindingAsync(
            SampleBinding with
            {
                ConnectorName = "different-connector",
            },
            CancellationToken.None
        );
        store.FailAllOperations = true;
        CdcReadBindingStateStoreResult failure = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        missing.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
        latched
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.Latched>()
            .Subject.State.Incident.Should()
            .Be(incident);
        alreadyLatched
            .Should()
            .BeOfType<CdcLatchIncidentStateStoreResult.AlreadyLatched>()
            .Subject.State.Incident.Should()
            .Be(incident);
        mismatch.Should().BeOfType<CdcExactMatchBindingStateStoreResult.BindingMismatch>();
        failure
            .Should()
            .BeOfType<CdcReadBindingStateStoreResult.StateStoreFailure>()
            .Subject.Failure.Kind.Should()
            .Be(CdcStateStoreFailureKind.LocalStateUnavailable);
    }

    [Test]
    public async Task It_models_guarded_import_list_and_delete_after_verified_cleanup()
    {
        InMemoryCdcBindingStateStore store = new();
        CdcAdoptionProof adoptionProof = CreateAdoptionProof(SampleBinding);
        CdcCleanupProof cleanupProof = CreateCleanupProof(SampleBinding);

        CdcImportBindingStateStoreResult imported = await store.ImportVerifiedBindingAsync(
            adoptionProof,
            CancellationToken.None
        );
        CdcListBindingsStateStoreResult listed = await store.ListBindingsAsync(
            SampleBinding.DeploymentKey,
            CancellationToken.None
        );
        CdcDeleteBindingStateStoreResult deleted = await store.DeleteStateAfterVerifiedCleanupAsync(
            cleanupProof,
            CancellationToken.None
        );
        CdcReadBindingStateStoreResult readAfterDelete = await store.ReadBindingAsync(
            SampleBinding.ToBindingIdentity(),
            CancellationToken.None
        );

        imported.Should().BeOfType<CdcImportBindingStateStoreResult.Imported>();
        listed
            .Should()
            .BeOfType<CdcListBindingsStateStoreResult.Listed>()
            .Subject.States.Should()
            .ContainSingle(state => state.Binding == SampleBinding);
        deleted
            .Should()
            .BeOfType<CdcDeleteBindingStateStoreResult.Deleted>()
            .Subject.BindingIdentity.Should()
            .Be(SampleBinding.ToCompleteBindingIdentity());
        readAfterDelete.Should().BeOfType<CdcReadBindingStateStoreResult.Missing>();
    }

    private static CdcIncident CreateIncident(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            CdcIncidentType.SourceHistoryContinuityLost,
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcIncidentFailureCategory.ConnectOffsetMissing,
            new CdcIncidentPositionMetadata(
                binding.ConnectorName,
                binding.TopicName,
                $"{binding.TopicName}.cdc-progress",
                null,
                "edfi_dms_dms_local_data_store_1_g1_slot",
                "sha256:9605ac115e4c82a0a9f1b2e7e0687c09fce12c699903be5189c8527efa3d2f40",
                "42",
                null,
                null,
                null,
                "40",
                "50",
                [CdcIncidentUnavailableFact.SchemaHistory]
            )
        );

    private static CdcAdoptionProof CreateAdoptionProof(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding,
            Enum.GetValues<CdcAdoptionVerificationKind>()
                .Select(kind => new CdcAdoptionVerificationResult(
                    kind,
                    CdcAdoptionVerificationState.ExactMatch,
                    "verified"
                ))
                .ToArray()
        );

    private static CdcCleanupProof CreateCleanupProof(CdcBinding binding) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            SampleObservedAt,
            binding.ToCompleteBindingIdentity(),
            CdcCleanupMode.RetireBindingGeneration,
            [
                new(
                    CdcGovernedArtifactKind.KafkaConnectConnector,
                    binding.ConnectorName,
                    CdcCleanupState.Deleted,
                    "deleted"
                ),
            ]
        );

    private sealed class InMemoryCdcBindingStateStore : ICdcBindingStateStore
    {
        private readonly Dictionary<CdcBindingIdentity, string> _bindingJsonByIdentity = [];
        private readonly Dictionary<CdcBindingIdentity, CdcIncident> _incidentByIdentity = [];

        public bool FailAllOperations { get; set; }

        public Task<CdcCreateBindingStateStoreResult> CreateBindingIfAbsentAsync(
            CdcBinding binding,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcCreateBindingStateStoreResult>(
                    new CdcCreateBindingStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            CdcBindingIdentity identity = binding.ToBindingIdentity();
            if (!_bindingJsonByIdentity.TryGetValue(identity, out string? existingJson))
            {
                _bindingJsonByIdentity.Add(identity, CdcJsonContract.Serialize(binding));
                return Task.FromResult<CdcCreateBindingStateStoreResult>(
                    new CdcCreateBindingStateStoreResult.Created(new(binding, ReadIncident(identity)))
                );
            }

            CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(binding, existingJson);
            return Task.FromResult<CdcCreateBindingStateStoreResult>(
                exactMatch.Succeeded
                    ? new CdcCreateBindingStateStoreResult.ExistingExactMatch(
                        new(exactMatch.PersistedBinding!, ReadIncident(identity))
                    )
                    : new CdcCreateBindingStateStoreResult.BindingMismatch(exactMatch.ToMismatch())
            );
        }

        public Task<CdcReadBindingStateStoreResult> ReadBindingAsync(
            CdcBindingIdentity identity,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcReadBindingStateStoreResult>(
                    new CdcReadBindingStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            if (!_bindingJsonByIdentity.TryGetValue(identity, out string? existingJson))
            {
                return Task.FromResult<CdcReadBindingStateStoreResult>(
                    new CdcReadBindingStateStoreResult.Missing(identity)
                );
            }

            CdcContractReadResult<CdcBinding> readResult = CdcJsonContract.Deserialize<CdcBinding>(
                existingJson
            );
            return Task.FromResult<CdcReadBindingStateStoreResult>(
                readResult.Succeeded
                    ? new CdcReadBindingStateStoreResult.Found(
                        new(readResult.Contract!, ReadIncident(identity))
                    )
                    : new CdcReadBindingStateStoreResult.StateStoreFailure(
                        CdcStateStoreFailure.InvalidPersistedBinding(readResult.Diagnostics)
                    )
            );
        }

        public Task<CdcExactMatchBindingStateStoreResult> ExactMatchBindingAsync(
            CdcBinding binding,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcExactMatchBindingStateStoreResult>(
                    new CdcExactMatchBindingStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            CdcBindingIdentity identity = binding.ToBindingIdentity();
            if (!_bindingJsonByIdentity.TryGetValue(identity, out string? existingJson))
            {
                return Task.FromResult<CdcExactMatchBindingStateStoreResult>(
                    new CdcExactMatchBindingStateStoreResult.BindingMissing(identity)
                );
            }

            CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(binding, existingJson);
            return Task.FromResult<CdcExactMatchBindingStateStoreResult>(
                exactMatch.Succeeded
                    ? new CdcExactMatchBindingStateStoreResult.ExactMatch(
                        new(exactMatch.PersistedBinding!, ReadIncident(identity))
                    )
                    : new CdcExactMatchBindingStateStoreResult.BindingMismatch(exactMatch.ToMismatch())
            );
        }

        public Task<CdcListBindingsStateStoreResult> ListBindingsAsync(
            string deploymentKey,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcListBindingsStateStoreResult>(
                    new CdcListBindingsStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            IReadOnlyList<CdcStoredBindingState> states = _bindingJsonByIdentity
                .Where(pair => pair.Key.DeploymentKey == deploymentKey)
                .Select(pair => new CdcStoredBindingState(
                    CdcJsonContract.Deserialize<CdcBinding>(pair.Value).Contract!,
                    ReadIncident(pair.Key)
                ))
                .ToArray();

            return Task.FromResult<CdcListBindingsStateStoreResult>(
                new CdcListBindingsStateStoreResult.Listed(states)
            );
        }

        public Task<CdcLatchIncidentStateStoreResult> LatchSourceHistoryLossAsync(
            CdcIncident incident,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcLatchIncidentStateStoreResult>(
                    new CdcLatchIncidentStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            CdcBindingIdentity identity = incident.BindingIdentity.ToBindingIdentity();
            if (!_bindingJsonByIdentity.TryGetValue(identity, out string? existingJson))
            {
                return Task.FromResult<CdcLatchIncidentStateStoreResult>(
                    new CdcLatchIncidentStateStoreResult.BindingMissing(identity)
                );
            }

            CdcBinding binding = CdcJsonContract.Deserialize<CdcBinding>(existingJson).Contract!;
            if (binding.ToCompleteBindingIdentity() != incident.BindingIdentity)
            {
                CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(binding, existingJson);
                return Task.FromResult<CdcLatchIncidentStateStoreResult>(
                    new CdcLatchIncidentStateStoreResult.BindingMismatch(exactMatch.ToMismatch())
                );
            }

            if (_incidentByIdentity.TryGetValue(identity, out CdcIncident? existingIncident))
            {
                return Task.FromResult<CdcLatchIncidentStateStoreResult>(
                    new CdcLatchIncidentStateStoreResult.AlreadyLatched(new(binding, existingIncident))
                );
            }

            _incidentByIdentity.Add(identity, incident);
            return Task.FromResult<CdcLatchIncidentStateStoreResult>(
                new CdcLatchIncidentStateStoreResult.Latched(new(binding, incident))
            );
        }

        public Task<CdcImportBindingStateStoreResult> ImportVerifiedBindingAsync(
            CdcAdoptionProof verifiedAdoptionProof,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcImportBindingStateStoreResult>(
                    new CdcImportBindingStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            CdcBinding binding = verifiedAdoptionProof.Binding;
            CdcBindingIdentity identity = binding.ToBindingIdentity();
            if (!_bindingJsonByIdentity.TryGetValue(identity, out string? existingJson))
            {
                _bindingJsonByIdentity.Add(identity, CdcJsonContract.Serialize(binding));
                return Task.FromResult<CdcImportBindingStateStoreResult>(
                    new CdcImportBindingStateStoreResult.Imported(new(binding, ReadIncident(identity)))
                );
            }

            CdcBindingExactMatchResult exactMatch = CdcBindingExactMatch.Compare(binding, existingJson);
            return Task.FromResult<CdcImportBindingStateStoreResult>(
                exactMatch.Succeeded
                    ? new CdcImportBindingStateStoreResult.ExistingExactMatch(
                        new(exactMatch.PersistedBinding!, ReadIncident(identity))
                    )
                    : new CdcImportBindingStateStoreResult.BindingMismatch(exactMatch.ToMismatch())
            );
        }

        public Task<CdcDeleteBindingStateStoreResult> DeleteStateAfterVerifiedCleanupAsync(
            CdcCleanupProof verifiedCleanupProof,
            CancellationToken cancellationToken
        )
        {
            if (FailAllOperations)
            {
                return Task.FromResult<CdcDeleteBindingStateStoreResult>(
                    new CdcDeleteBindingStateStoreResult.StateStoreFailure(CreateFailure())
                );
            }

            CdcCompleteBindingIdentity completeIdentity = verifiedCleanupProof.BindingIdentity;
            CdcBindingIdentity identity = completeIdentity.ToBindingIdentity();
            bool removedBinding = _bindingJsonByIdentity.Remove(identity);
            _incidentByIdentity.Remove(identity);

            return Task.FromResult<CdcDeleteBindingStateStoreResult>(
                removedBinding
                    ? new CdcDeleteBindingStateStoreResult.Deleted(completeIdentity)
                    : new CdcDeleteBindingStateStoreResult.BindingMissing(completeIdentity)
            );
        }

        private CdcIncident? ReadIncident(CdcBindingIdentity identity) =>
            _incidentByIdentity.GetValueOrDefault(identity);

        private static CdcStateStoreFailure CreateFailure() =>
            CdcStateStoreFailure.LocalStateUnavailable("$", "CDC state store is unavailable.");
    }
}
