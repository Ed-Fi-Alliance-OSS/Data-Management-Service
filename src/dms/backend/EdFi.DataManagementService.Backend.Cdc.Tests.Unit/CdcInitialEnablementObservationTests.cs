// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(false)]
[TestFixture(true)]
public class Given_CdcInitialEnablementProviderObservation(bool sqlServer)
{
    private static readonly DocumentCacheTargetKey Target = DocumentCacheTargetKey.Create("tenant", 1);
    private static readonly DocumentCachePhysicalSourceFingerprint Fingerprint = new(
        "sha256:" + new string('a', 64)
    );
    private ServiceProvider _provider = null!;
    private IDocumentCacheTargetRegistry _registry = null!;
    private IDocumentCachePhysicalSourceFingerprintReader _fingerprints = null!;
    private IDocumentCacheAdministrativePrimitives _primitives = null!;
    private IDocumentCacheAdministrativeMutex _mutex = null!;
    private IDocumentCacheAdministrativeMutexLease _lease = null!;
    private IRelationalWriteSession _transaction = null!;
    private DocumentCacheTargetExecutionContext _context = null!;
    private List<string> _trace = null!;

    [SetUp]
    public void Setup()
    {
        _trace = [];
        var provider = sqlServer ? RelationalProviderToken.SqlServer : RelationalProviderToken.Postgresql;
        _context = new(
            Target,
            new(1),
            DocumentCacheTargetEffectiveSettings.FromOptions(new()),
            new(1, provider.Value),
            new(provider, "private-connection"),
            Fingerprint,
            new(DocumentCacheLifecycleState.Disabled, false),
            new(DocumentCacheInventoryStatus.Satisfied, "Satisfied"),
            new(DocumentCacheEnqueueTriggerStatus.Satisfied, "Satisfied"),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
        _registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() =>
                _registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .Invokes(() => _trace.Add("resolve"))
            .Returns(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.ResolvedEligible(
                            Target,
                            _context.EffectiveSettings,
                            _context.Generation,
                            provider,
                            Fingerprint,
                            _context.Lifecycle,
                            _context.Inventory,
                            _context.EnqueueTrigger,
                            _context.SqlServerPrerequisites
                        ),
                    ],
                    DateTimeOffset.UtcNow
                )
            );
        A.CallTo(() => _registry.CurrentRuntimeSnapshot)
            .Returns(new DocumentCacheTargetRuntimeSnapshot([_context], DateTimeOffset.UtcNow));
        _fingerprints = A.Fake<IDocumentCachePhysicalSourceFingerprintReader>();
        A.CallTo(() =>
                _fingerprints.ReadFingerprintAsync(_context.ConnectionInput.Value, A<CancellationToken>._)
            )
            .Invokes(() => _trace.Add("source"))
            .Returns(DocumentCachePhysicalSourceFingerprintReadResult.Success(Fingerprint));
        _primitives = A.Fake<IDocumentCacheAdministrativePrimitives>();
        _mutex = A.Fake<IDocumentCacheAdministrativeMutex>();
        _lease = A.Fake<IDocumentCacheAdministrativeMutexLease>();
        _transaction = A.Fake<IRelationalWriteSession>();
        A.CallTo(() => _mutex.AcquireAsync(_context.ConnectionInput, A<CancellationToken>._))
            .Invokes(() => _trace.Add("mutex"))
            .Returns(_lease);
        A.CallTo(() => _lease.BeginTransactionAsync(IsolationLevel.Serializable, A<CancellationToken>._))
            .Invokes(() => _trace.Add("transaction"))
            .Returns(_transaction);
        A.CallTo(() =>
                _primitives.ReadLifecycleAsync(
                    _transaction,
                    DocumentCacheAdministrativeStateLockMode.Shared,
                    A<CancellationToken>._
                )
            )
            .Invokes(() => _trace.Add("lifecycle"))
            .Returns(
                DocumentCacheLifecycleReadResult.Success(new(DocumentCacheLifecycleState.Tracking, true))
            );
        A.CallTo(() =>
                _primitives.ReadGuardedNewEmptyActivationStateAsync(_transaction, A<CancellationToken>._)
            )
            .Invokes(() => _trace.Add("tables"))
            .Returns(new DocumentCacheGuardedNewEmptyActivationState(false, true, false));
        A.CallTo(() => _primitives.ValidateActivationPrerequisitesAsync(_transaction, A<CancellationToken>._))
            .Invokes(() => _trace.Add("prerequisites"))
            .Returns(
                DocumentCacheProviderPrerequisiteValidationResult.ActivationPreflight(
                    DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
                )
            );
        A.CallTo(() => _transaction.RollbackAsync(A<CancellationToken>._))
            .Invokes(() => _trace.Add("rollback"));
        A.CallTo(() => _transaction.DisposeAsync()).Invokes(() => _trace.Add("dispose-transaction"));
        A.CallTo(() => _lease.DisposeAsync()).Invokes(() => _trace.Add("dispose-mutex"));
        var services = new ServiceCollection();
        services
            .AddSingleton(_registry)
            .AddSingleton(_fingerprints)
            .AddSingleton(_primitives)
            .AddSingleton(_mutex);
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public async Task Teardown()
    {
        await _transaction.DisposeAsync();
        await _lease.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Test]
    public async Task It_reads_fresh_source_and_consistent_lifecycle_latch_and_tables_using_existing_primitives()
    {
        var before = DateTimeOffset.UtcNow;
        var result = await CdcInitialDatabaseInspector.ObserveAsync(
            _provider,
            Target,
            CancellationToken.None
        );
        result.ObservedAt.Should().BeOnOrAfter(before);
        result.PhysicalSourceFingerprint.Should().Be(Fingerprint.Value);
        result
            .Lifecycle.Should()
            .Be(new DocumentCacheLifecycleObservation(DocumentCacheLifecycleState.Tracking, true));
        result.Tables.IsEmpty.Should().BeFalse();
        result.Tables.CanonicalDocumentsEmpty.Should().BeFalse();
        result.Tables.DocumentCacheEmpty.Should().BeTrue();
        result.Tables.DocumentProjectionWorkEmpty.Should().BeFalse();
        _trace
            .Should()
            .Equal(
                "resolve",
                "source",
                "mutex",
                "transaction",
                "lifecycle",
                "tables",
                "prerequisites",
                "rollback",
                "dispose-transaction",
                "dispose-mutex"
            );
        A.CallTo(() => _transaction.CommitAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() =>
                _primitives.TryTransitionLifecycleAsync(
                    A<IRelationalWriteSession>._,
                    A<DocumentCacheAdministrativeLifecycleTransitionRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_source_change_before_entering_the_provider_mutex()
    {
        A.CallTo(() =>
                _fingerprints.ReadFingerprintAsync(_context.ConnectionInput.Value, A<CancellationToken>._)
            )
            .Returns(
                DocumentCachePhysicalSourceFingerprintReadResult.Success(new("sha256:" + new string('b', 64)))
            );
        Func<Task> observe = () =>
            CdcInitialDatabaseInspector.ObserveAsync(_provider, Target, CancellationToken.None);
        await observe.Should().ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() => _mutex.AcquireAsync(A<DocumentCacheTargetConnectionInput>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_rejects_lost_runtime_membership_before_provider_reads()
    {
        A.CallTo(() => _registry.CurrentRuntimeSnapshot)
            .Returns(new DocumentCacheTargetRuntimeSnapshot([], DateTimeOffset.UtcNow));
        Func<Task> observe = () =>
            CdcInitialDatabaseInspector.ObserveAsync(_provider, Target, CancellationToken.None);
        await observe.Should().ThrowAsync<CdcWorkflowStateException>();
        A.CallTo(() => _fingerprints.ReadFingerprintAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_disposes_the_transaction_and_mutex_on_provider_failure()
    {
        A.CallTo(() =>
                _primitives.ReadGuardedNewEmptyActivationStateAsync(_transaction, A<CancellationToken>._)
            )
            .ThrowsAsync(new IOException("provider unavailable"));
        Func<Task> observe = () =>
            CdcInitialDatabaseInspector.ObserveAsync(_provider, Target, CancellationToken.None);
        await observe.Should().ThrowAsync<IOException>();
        _trace.TakeLast(2).Should().Equal("dispose-transaction", "dispose-mutex");
    }

    [Test]
    public async Task It_forwards_cancellation_to_all_provider_reads()
    {
        using var cancellation = new CancellationTokenSource();
        await CdcInitialDatabaseInspector.ObserveAsync(_provider, Target, cancellation.Token);
        A.CallTo(() =>
                _primitives.ReadLifecycleAsync(
                    _transaction,
                    DocumentCacheAdministrativeStateLockMode.Shared,
                    cancellation.Token
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _primitives.ReadGuardedNewEmptyActivationStateAsync(_transaction, cancellation.Token))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _primitives.ValidateActivationPrerequisitesAsync(_transaction, cancellation.Token))
            .MustHaveHappenedOnceExactly();
    }
}
