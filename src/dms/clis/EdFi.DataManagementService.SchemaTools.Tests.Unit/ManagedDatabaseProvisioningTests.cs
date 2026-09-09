// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_Managed_Database_Provisioning
{
    private string _root = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private ICdcManagedDatabaseProvisioner _provider = null!;
    private CdcManagedDatabaseProvisioning _controller = null!;
    private static readonly CdcTargetIdentity Target = new(
        "local",
        "default",
        "42",
        "datastore-42",
        1,
        CdcProvider.Postgresql
    );
    private const string Fingerprint =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "creation-receipt-" + Guid.NewGuid().ToString("N"));
        _store = new(_root);
        _provider = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => _provider.CreateDatabase()).Returns(true);
        A.CallTo(() => _provider.ReadSourceFingerprintAsync(A<CancellationToken>._)).Returns(Fingerprint);
        _controller = new(_store);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private async Task<CdcWorkflowJournal> ReadAsync()
    {
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            default
        );
        return await session.ReadAsync(Target, default);
    }

    private string ReadDurableJson() =>
        File.ReadAllText(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single());

    [TestCase(CdcWorkflowPurpose.SourceHistoryOnly)]
    [TestCase(CdcWorkflowPurpose.InitialCdcProvisioning)]
    public async Task It_persists_creation_purpose_before_CREATE_and_preserves_it_on_retry(
        CdcWorkflowPurpose purpose
    )
    {
        A.CallTo(() => _provider.CreateDatabase())
            .Invokes(() =>
            {
                using var json = JsonDocument.Parse(ReadDurableJson());
                json.RootElement.GetProperty("purpose").GetString().Should().Be(purpose.ToString());
            })
            .Returns(true);
        var first = await _controller.ProvisionAsync(Target, _provider, purpose: purpose);
        (await _controller.ProvisionAsync(Target, _provider, purpose: purpose)).Should().Be(first);
        (await ReadAsync()).Purpose.Should().Be(purpose);
        A.CallTo(() => _provider.CreateDatabase()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _provider.ProvisionSchema(true)).MustHaveHappenedOnceExactly();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_never_promotes_source_history_only_creation_even_after_interruption(bool interrupted)
    {
        if (interrupted)
        {
            A.CallTo(() => _provider.ProvisionSchema(true)).Throws(new IOException("interrupted"));
            await FluentActions
                .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
                .Should()
                .ThrowAsync<IOException>();
        }
        else
        {
            await _controller.ProvisionAsync(Target, _provider);
        }
        (await ReadAsync()).Purpose.Should().Be(CdcWorkflowPurpose.SourceHistoryOnly);
        Fake.ClearRecordedCalls(_provider);
        await FluentActions
            .Awaiting(() =>
                _controller.ProvisionAsync(
                    Target,
                    _provider,
                    purpose: CdcWorkflowPurpose.InitialCdcProvisioning
                )
            )
            .Should()
            .ThrowAsync<CdcWorkflowStateException>()
            .Where(e => e.Failure == CdcWorkflowStateFailure.Contradictory);
        Fake.GetCalls(_provider).Should().BeEmpty();
        (await ReadAsync()).Purpose.Should().Be(CdcWorkflowPurpose.SourceHistoryOnly);
    }

    [TestCase(true, CdcDatabaseCreationOutcome.Created)]
    [TestCase(false, CdcDatabaseCreationOutcome.Reused)]
    public async Task It_records_the_actual_provider_outcome_before_schema_effects(
        bool created,
        CdcDatabaseCreationOutcome outcome
    )
    {
        A.CallTo(() => _provider.CreateDatabase())
            .Invokes(() =>
            {
                using var json = JsonDocument.Parse(ReadDurableJson());
                var operation = json.RootElement.GetProperty("operations")[0];
                operation.GetProperty("effect").GetString().Should().Be("CreateDatabase");
                operation.GetProperty("completions").GetArrayLength().Should().Be(0);
            })
            .Returns(created);
        A.CallTo(() => _provider.ProvisionSchema(created))
            .Invokes(() =>
            {
                using var json = JsonDocument.Parse(ReadDurableJson());
                json.RootElement.GetProperty("operations")[0]
                    .GetProperty("completions")[0]
                    .GetProperty("evidence")
                    .GetProperty("receipt")
                    .GetProperty("outcome")
                    .GetString()
                    .Should()
                    .Be(outcome.ToString());
            });
        var result = await _controller.ProvisionAsync(Target, _provider);
        result.CreationReceipt.Outcome.Should().Be(outcome);
        result.PhysicalSourceFingerprint.Should().Be(Fingerprint);
        result.Target.Should().Be(Target);
        (await ReadAsync())
            .Operations.Select(o => o.Effect)
            .Should()
            .Equal(CdcWorkflowEffect.CreateDatabase, CdcWorkflowEffect.AssociateSource);
        A.CallTo(() => _provider.ProvisionSchema(created)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_retains_creation_receipt_when_ddl_fails_and_rejects_unverifiable_retry()
    {
        A.CallTo(() => _provider.ProvisionSchema(true)).Throws(new InvalidOperationException("DDL failure"));
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        var journal = await ReadAsync();
        ((CdcWorkflowCompletion.Database)journal.Operations[0].Completions.Single().Evidence)
            .Receipt.Outcome.Should()
            .Be(CdcDatabaseCreationOutcome.Created);
        Fake.ClearRecordedCalls(_provider);
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<CdcManagedProvisioningRecoveryException>();
        A.CallTo(() => _provider.CreateDatabase()).MustNotHaveHappened();
        A.CallTo(() => _provider.ReadSourceFingerprintAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_creation_with_lost_receipt_without_relabeling_existence()
    {
        A.CallTo(() => _provider.CreateDatabase()).Throws(new IOException("lost CREATE response"));
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<IOException>();
        (await ReadAsync()).Operations[0].Completions.Should().BeEmpty();
        A.CallTo(() => _provider.CreateDatabase()).Returns(false);
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<CdcManagedProvisioningRecoveryException>();
        A.CallTo(() => _provider.CreateDatabase()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_checks_current_source_on_retry_without_creating_another_receipt()
    {
        var initial = await _controller.ProvisionAsync(Target, _provider);
        var retry = await _controller.ProvisionAsync(Target, _provider);
        retry.Should().Be(initial);
        A.CallTo(() => _provider.CreateDatabase()).MustHaveHappenedOnceExactly();
        A.CallTo(() => _provider.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_validates_requested_schema_compatibility_on_retry()
    {
        await _controller.ProvisionAsync(Target, _provider);
        A.CallTo(() => _provider.ValidateSchema()).Throws(new InvalidOperationException("schema mismatch"));
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        A.CallTo(() => _provider.CreateDatabase()).MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_rejects_a_different_physical_source_on_retry()
    {
        await _controller.ProvisionAsync(Target, _provider);
        Fake.ClearRecordedCalls(_provider);
        A.CallTo(() => _provider.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns("sha256:" + new string('b', 64));
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() => _provider.ProvisionSchema(A<bool>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_persists_the_creation_receipt_even_if_cancellation_arrives_after_create()
    {
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => _provider.CreateDatabase()).Invokes(cancellation.Cancel).Returns(true);
        var exception = await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider, cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
        (await ReadAsync()).Operations[0].Completions.Should().ContainSingle();
        A.CallTo(() => _provider.ProvisionSchema(A<bool>._)).MustNotHaveHappened();
    }

    [Test]
    public async Task It_keeps_unavailable_source_evidence_incomplete_and_sanitized()
    {
        A.CallTo(() => _provider.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .ThrowsAsync(new IOException("private-source-and-credentials"));
        var failure = await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        failure.Which.Failure.Should().Be(CdcWorkflowStateFailure.Unavailable);
        failure.Which.ToString().Should().NotContain("private-source-and-credentials");
        var journal = await ReadAsync();
        journal.Operations[0].Completions.Should().ContainSingle();
        journal.Operations[1].Completions.Should().BeEmpty();
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target, _provider))
            .Should()
            .ThrowAsync<CdcManagedProvisioningRecoveryException>();
    }

    [Test]
    public async Task It_rejects_existing_provenance_for_another_normalized_target()
    {
        await _controller.ProvisionAsync(Target, _provider);
        Fake.ClearRecordedCalls(_provider);
        await FluentActions
            .Awaiting(() => _controller.ProvisionAsync(Target with { DataStoreId = "43" }, _provider))
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() => _provider.CreateDatabase()).MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_reprovisioning_after_binding_intent()
    {
        var result = await _controller.ProvisionAsync(
            Target,
            _provider,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10),
                default
            )
        )
        {
            await session.RecordIntentAsync(
                Target,
                result.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.ReserveBinding,
                [],
                default
            );
        }
        Fake.ClearRecordedCalls(_provider);
        await FluentActions
            .Awaiting(() =>
                _controller.ProvisionAsync(
                    Target,
                    _provider,
                    purpose: CdcWorkflowPurpose.InitialCdcProvisioning
                )
            )
            .Should()
            .ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() => _provider.ReadSourceFingerprintAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }
}
