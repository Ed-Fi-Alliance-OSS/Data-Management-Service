// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator shell (Step 1.4) over a fake readiness, a spy engine, a fake catalog, and fake
/// holders: readiness is refused before any port is touched; the boundary is refused before the lock;
/// artifact resolution and pin equality run before the lock; lock timeout and infrastructure failure
/// are reported without any further engine call; the holder is released on every path including
/// exceptions; loss of the holder ends the operation as transient <c>LockLost</c> with no further engine
/// call, whether the read in flight honours the token, completes normally, or throws; a deadline is
/// <c>TimedOut</c>; cancellation propagates; and a delete that finds the name empty mutates nothing.
/// </summary>
public class DmsDataStoreProvisioningServiceShellTests
{
    [TestFixture]
    public class Given_readiness_is_not_ready
    {
        private Harness _harness = null!;
        private DmsArtifactResolution _resolution = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                new DmsProvisionerReadiness.NotReady([
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdapterUnavailable,
                        "AdapterResolution"
                    ),
                ])
            );
            _resolution = await _harness.Service.ResolveArtifactAsync(
                DmsDatabaseTemplate.Minimal,
                CancellationToken.None
            );
            _create = await _harness.Service.CreateAsync(CreateRequest(), CancellationToken.None);
            _delete = await _harness.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);
        }

        [Test]
        public void It_refuses_every_call_as_not_ready()
        {
            _resolution
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Which.Detail.Code.Should()
                .Be(DmsProvisioningFailureCode.AdapterUnavailable);
            FailureOf(_create).Category.Should().Be(DmsProvisioningFailureCategory.NotReady);
            FailureOf(_delete).Category.Should().Be(DmsProvisioningFailureCategory.NotReady);
        }

        [Test]
        public void It_touches_neither_the_catalog_nor_the_engine() =>
            (_harness.Catalog.Calls, _harness.Engine.Calls.Count).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_a_create_request_fails_boundary_validation
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _nullTemplate = null!;
        private DmsDataStoreCreateResult _defaultName = null!;
        private DmsDataStoreCreateResult _protectedName = null!;
        private DmsDataStoreCreateResult _emptyIdentity = null!;
        private DmsDataStoreCreateResult _missingPin = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness(
                protectedNames: ProtectedDatabaseNames.Create("edfi_configurationservice", ["EdFi_Identity"])
            );
            DmsDataStoreCreateRequest valid = CreateRequest();
            _nullTemplate = await _harness.Service.CreateAsync(
                valid with
                {
                    Template = null!,
                },
                CancellationToken.None
            );
            _defaultName = await _harness.Service.CreateAsync(
                valid with
                {
                    TargetName = default,
                },
                CancellationToken.None
            );
            _protectedName = await _harness.Service.CreateAsync(
                valid with
                {
                    TargetName = Name("EdFi_Identity"),
                },
                CancellationToken.None
            );
            _emptyIdentity = await _harness.Service.CreateAsync(
                valid with
                {
                    ExpectedSourceIdentity = Guid.Empty,
                },
                CancellationToken.None
            );
            _missingPin = await _harness.Service.CreateAsync(
                valid with
                {
                    PinnedArtifact = null!,
                },
                CancellationToken.None
            );
        }

        [Test]
        public void It_rejects_a_missing_template() =>
            FailureOf(_nullTemplate).Code.Should().Be(DmsProvisioningFailureCode.TemplateNotAllowlisted);

        [Test]
        public void It_rejects_an_unconstructed_name() =>
            FailureOf(_defaultName).Code.Should().Be(DmsProvisioningFailureCode.InvalidTargetName);

        [Test]
        public void It_rejects_a_name_the_provisioner_protects_even_when_its_creator_did_not() =>
            FailureOf(_protectedName).Code.Should().Be(DmsProvisioningFailureCode.InvalidTargetName);

        [Test]
        public void It_rejects_an_empty_source_identity() =>
            FailureOf(_emptyIdentity).Code.Should().Be(DmsProvisioningFailureCode.EmptySourceIdentity);

        [Test]
        public void It_rejects_a_missing_pin() =>
            FailureOf(_missingPin).Code.Should().Be(DmsProvisioningFailureCode.ArtifactPinMissing);

        [Test]
        public void It_classifies_every_rejection_as_invalid_request_at_the_boundary()
        {
            foreach (
                DmsDataStoreCreateResult result in new[]
                {
                    _nullTemplate,
                    _defaultName,
                    _protectedName,
                    _emptyIdentity,
                    _missingPin,
                }
            )
            {
                DmsProvisioningFailure failure = FailureOf(result);
                failure.Category.Should().Be(DmsProvisioningFailureCategory.InvalidRequest);
                failure.Diagnostic.Operation.Should().Be(DmsDataStoreProvisioningService.BoundaryStep);
            }
        }

        [Test]
        public void It_touches_neither_the_catalog_nor_the_engine() =>
            (_harness.Catalog.Calls, _harness.Engine.Calls.Count).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_a_delete_request_fails_boundary_validation
    {
        private Harness _harness = null!;
        private DmsDataStoreDeleteResult _defaultName = null!;
        private DmsDataStoreDeleteResult _emptyIdentity = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness();
            _defaultName = await _harness.Service.DeleteAsync(
                new DmsDataStoreDeleteRequest(default, Guid.NewGuid()),
                CancellationToken.None
            );
            _emptyIdentity = await _harness.Service.DeleteAsync(
                new DmsDataStoreDeleteRequest(Name("EdFi_Ods_Test"), Guid.Empty),
                CancellationToken.None
            );
        }

        [Test]
        public void It_rejects_an_unconstructed_name() =>
            FailureOf(_defaultName).Code.Should().Be(DmsProvisioningFailureCode.InvalidTargetName);

        [Test]
        public void It_rejects_an_empty_source_identity() =>
            FailureOf(_emptyIdentity).Code.Should().Be(DmsProvisioningFailureCode.EmptySourceIdentity);

        [Test]
        public void It_never_reaches_the_engine() => _harness.Engine.Calls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_admin_connection_cannot_be_resolved
    {
        private static readonly DmsProvisioningDiagnostic _diagnostic = new(
            "System.TimeoutException",
            null,
            DmsAdminConnectionSource.ResolverFailedStep
        );

        private Harness _failing = null!;
        private Harness _resolving = null!;
        private Harness _notReady = null!;
        private Harness _invalid = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;

        [SetUp]
        public async Task Setup()
        {
            _failing = new Harness();
            _failing.AdminConnection.Result = new DmsAdminConnectionResolution.Failed(_diagnostic);
            _create = await _failing.Service.CreateAsync(CreateRequest(), CancellationToken.None);
            _delete = await _failing.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);

            // Resolved, then refused at the lock, so each operation is seen resolving exactly once.
            _resolving = new Harness();
            _resolving.Engine.OnAcquireTargetLock = _ =>
                Task.FromResult<DmsTargetLockResult>(new DmsTargetLockResult.Timeout());
            await _resolving.Service.CreateAsync(CreateRequest(), CancellationToken.None);
            await _resolving.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);

            _notReady = new Harness(
                new DmsProvisionerReadiness.NotReady([
                    new DmsReadinessReason(DmsProvisioningFailureCode.StartupValidationFailed, "Probe"),
                ])
            );
            await _notReady.Service.CreateAsync(CreateRequest(), CancellationToken.None);
            await _notReady.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);

            _invalid = new Harness();
            await _invalid.Service.DeleteAsync(
                new DmsDataStoreDeleteRequest(Name("EdFi_Ods_Test"), Guid.Empty),
                CancellationToken.None
            );
        }

        [Test]
        public void It_fails_the_create_as_transient_secret_resolution_failed_with_the_diagnostic() =>
            FailureOf(_create)
                .Should()
                .Match<DmsProvisioningFailure>(failure =>
                    failure.Code == DmsProvisioningFailureCode.SecretResolutionFailed
                    && failure.Category == DmsProvisioningFailureCategory.Transient
                    && failure.Diagnostic == _diagnostic
                );

        [Test]
        public void It_fails_the_delete_as_transient_secret_resolution_failed_with_the_diagnostic() =>
            FailureOf(_delete)
                .Should()
                .Match<DmsProvisioningFailure>(failure =>
                    failure.Code == DmsProvisioningFailureCode.SecretResolutionFailed
                    && failure.Category == DmsProvisioningFailureCategory.Transient
                    && failure.Diagnostic == _diagnostic
                );

        [Test]
        public void It_stages_no_artifact_and_never_reaches_the_engine() =>
            (_failing.Catalog.Calls, _failing.Engine.Calls.Count).Should().Be((0, 0));

        [Test]
        public void It_resolves_once_per_operation_before_the_lock() =>
            (_resolving.AdminConnection.Resolutions, _resolving.Engine.Members.Distinct().Single())
                .Should()
                .Be((2, "AcquireTargetLock"));

        [Test]
        public void It_resolves_nothing_for_a_call_refused_by_readiness_or_the_boundary() =>
            (_notReady.AdminConnection.Resolutions, _invalid.AdminConnection.Resolutions).Should().Be((0, 0));
    }

    [TestFixture]
    public class Given_artifact_resolution_runs_before_the_lock
    {
        private Harness _refusing = null!;
        private Harness _changed = null!;
        private DmsArtifactResolution _resolution = null!;
        private DmsDataStoreCreateResult _refused = null!;
        private DmsDataStoreCreateResult _pinChanged = null!;
        private DmsProvisioningFailure _catalogFailure = null!;
        private bool _directoryReleased;

        [SetUp]
        public async Task Setup()
        {
            _catalogFailure = DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.ArtifactUntrusted,
                DmsProvisioningDiagnostic.ForOperation("VerifyAttestation")
            );
            _refusing = new Harness();
            _refusing.Catalog.OnResolve = _ => new DmsArtifactResolution.Failure(
                _catalogFailure,
                DmsCleanupReport.Clean
            );
            _refusing.Catalog.OnStage = (_, _, _) =>
                Task.FromResult<TemplateArtifactStaging>(
                    new TemplateArtifactStaging.Failure(_catalogFailure, DmsCleanupReport.Clean)
                );
            _resolution = await _refusing.Service.ResolveArtifactAsync(
                DmsDatabaseTemplate.Sample,
                CancellationToken.None
            );
            _refused = await _refusing.Service.CreateAsync(CreateRequest(), CancellationToken.None);

            // The catalog stages the real package; the request carries a pin that names another one.
            _changed = new Harness();
            _pinChanged = await _changed.Service.CreateAsync(
                CreateRequest(pin: Pin("e")),
                CancellationToken.None
            );
            _directoryReleased = SharedTemplateCatalog.Workspace.OperationEntries.Length == 0;
        }

        [Test]
        public void It_passes_the_catalog_verdict_through_on_resolve() =>
            _resolution
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Which.Detail.Should()
                .BeSameAs(_catalogFailure);

        [Test]
        public void It_fails_the_create_with_the_catalog_verdict() =>
            FailureOf(_refused).Should().BeSameAs(_catalogFailure);

        [Test]
        public void It_fails_the_create_when_the_resolved_pin_differs_from_the_request()
        {
            DmsProvisioningFailure failure = FailureOf(_pinChanged);
            (failure.Code, failure.Category)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired
                    )
                );
        }

        [Test]
        public void It_never_reaches_the_engine() =>
            (_refusing.Engine.Calls.Count, _changed.Engine.Calls.Count).Should().Be((0, 0));

        [Test]
        public void It_releases_the_staged_artifact_when_the_pin_differs() =>
            _directoryReleased.Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_artifact_release_leaves_residue_and_the_create_fails_under_the_lock
    {
        private DmsDataStoreCreateResult _lockTimedOut = null!;
        private DmsDataStoreCreateResult _protectedTarget = null!;
        private DmsDataStoreCreateResult _lockLost = null!;

        /// <summary>Runs one create whose operation directory is held open while the artifact is released, then frees it.</summary>
        private static async Task<DmsDataStoreCreateResult> WithTrappedReleaseAsync(Harness harness)
        {
            DeletionTrap? trap = null;
            PrivateOperationDirectory? trapped = null;
            SharedTemplateCatalog.Catalog.BeforeRelease = directory =>
            {
                trapped = directory;
                trap = new DeletionTrap(directory.Path);
            };
            try
            {
                return await harness.Service.CreateAsync(CreateRequest(), CancellationToken.None);
            }
            finally
            {
                SharedTemplateCatalog.Catalog.BeforeRelease = null;
                trap?.Dispose();
                trapped?.TryDelete();
            }
        }

        [SetUp]
        public async Task Setup()
        {
            Harness timeout = new();
            timeout.Engine.OnAcquireTargetLock = _ =>
                Task.FromResult<DmsTargetLockResult>(new DmsTargetLockResult.Timeout());
            _lockTimedOut = await WithTrappedReleaseAsync(timeout);

            Harness protectedTarget = new();
            protectedTarget.Engine.OnInspect = _ =>
                Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Protected());
            _protectedTarget = await WithTrappedReleaseAsync(protectedTarget);

            Harness lost = new();
            lost.Engine.OnInspect = _ =>
            {
                lost.OnlyHolder.Lose();
                return Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            };
            _lockLost = await WithTrappedReleaseAsync(lost);
        }

        private static void ShouldCarryDirectoryResidue(DmsDataStoreCreateResult result) =>
            CleanupOf(result)
                .Residue.Should()
                .Contain(residue => residue.Kind == DmsResidueKind.OperationDirectory);

        [Test]
        public void It_keeps_the_residue_through_a_lock_timeout()
        {
            FailureOf(_lockTimedOut).Code.Should().Be(DmsProvisioningFailureCode.LockTimeout);
            ShouldCarryDirectoryResidue(_lockTimedOut);
        }

        [Test]
        public void It_keeps_the_residue_through_a_step_failure()
        {
            FailureOf(_protectedTarget).Code.Should().Be(DmsProvisioningFailureCode.ProtectedTarget);
            ShouldCarryDirectoryResidue(_protectedTarget);
        }

        [Test]
        public void It_keeps_the_residue_through_lock_loss()
        {
            FailureOf(_lockLost).Code.Should().Be(DmsProvisioningFailureCode.LockLost);
            ShouldCarryDirectoryResidue(_lockLost);
        }
    }

    [TestFixture]
    public class Given_the_target_lock_cannot_be_acquired
    {
        private Harness _timeout = null!;
        private Harness _failure = null!;
        private DmsDataStoreCreateResult _timedOut = null!;
        private DmsDataStoreDeleteResult _unavailable = null!;
        private DmsProvisioningDiagnostic _lockDiagnostic = null!;

        [SetUp]
        public async Task Setup()
        {
            _timeout = new Harness();
            _timeout.Engine.OnAcquireTargetLock = _ =>
                Task.FromResult<DmsTargetLockResult>(new DmsTargetLockResult.Timeout());
            _timedOut = await _timeout.Service.CreateAsync(CreateRequest(), CancellationToken.None);

            _lockDiagnostic = new DmsProvisioningDiagnostic(
                "Npgsql.NpgsqlException",
                "08006",
                "OpenHolderSession"
            );
            _failure = new Harness();
            _failure.Engine.OnAcquireTargetLock = _ =>
                Task.FromResult<DmsTargetLockResult>(new DmsTargetLockResult.Failure(_lockDiagnostic));
            _unavailable = await _failure.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);
        }

        [Test]
        public void It_reports_a_timeout_as_transient_lock_timeout()
        {
            DmsProvisioningFailure failure = FailureOf(_timedOut);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockTimeout,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.AcquireTargetLockStep
                    )
                );
        }

        [Test]
        public void It_neither_identifies_nor_terminates_the_current_holder() =>
            _timeout.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);

        [Test]
        public void It_reports_an_infrastructure_failure_as_lock_unavailable_with_the_engine_diagnostic()
        {
            DmsProvisioningFailure failure = FailureOf(_unavailable);
            failure.Code.Should().Be(DmsProvisioningFailureCode.LockUnavailable);
            failure.Diagnostic.Should().BeSameAs(_lockDiagnostic);
            _failure.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
        }
    }

    [TestFixture]
    public class Given_the_caller_cancels_during_lock_acquisition
    {
        private Harness _harness = null!;
        private Func<Task> _create = () => Task.CompletedTask;
        private Func<Task> _delete = () => Task.CompletedTask;

        [SetUp]
        public void Setup()
        {
            _harness = new Harness();
            _harness.Engine.OnAcquireTargetLock = async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new DmsTargetLockResult.Timeout();
            };
            _create = async () =>
            {
                using CancellationTokenSource cancellation = new();
                Task<DmsDataStoreCreateResult> create = _harness.Service.CreateAsync(
                    CreateRequest(),
                    cancellation.Token
                );
                await cancellation.CancelAsync();
                await create;
            };
            _delete = async () =>
            {
                using CancellationTokenSource cancellation = new();
                Task<DmsDataStoreDeleteResult> delete = _harness.Service.DeleteAsync(
                    DeleteRequest(),
                    cancellation.Token
                );
                await cancellation.CancelAsync();
                await delete;
            };
        }

        [Test]
        public async Task It_propagates_the_cancellation_from_create() =>
            await _create.Should().ThrowAsync<OperationCanceledException>();

        [Test]
        public async Task It_propagates_the_cancellation_from_delete() =>
            await _delete.Should().ThrowAsync<OperationCanceledException>();

        [Test]
        public async Task It_acquired_nothing_to_release()
        {
            await _create.Should().ThrowAsync<OperationCanceledException>();
            _harness.Holders.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_target_is_protected_or_fenced
    {
        private Harness _protected = null!;
        private Harness _fenced = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;

        [SetUp]
        public async Task Setup()
        {
            _protected = new Harness();
            _protected.Engine.OnInspect = _ =>
                Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Protected());
            _create = await _protected.Service.CreateAsync(CreateRequest(), CancellationToken.None);

            _fenced = new Harness();
            _fenced.Engine.OnInspect = _ =>
                Task.FromResult<DmsLiveTargetState>(
                    new DmsLiveTargetState.Fenced(new DmsInstanceIdentity.Postgresql(16385, "claim"))
                );
            _delete = await _fenced.Service.DeleteAsync(DeleteRequest(), CancellationToken.None);
        }

        [Test]
        public void It_reports_a_protected_target_as_permanent()
        {
            DmsProvisioningFailure failure = FailureOf(_create);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ProtectedTarget,
                        DmsProvisioningFailureCategory.Permanent,
                        DmsDataStoreProvisioningService.InspectTargetStep
                    )
                );
        }

        [Test]
        public void It_reports_a_fenced_target_for_inspection()
        {
            DmsProvisioningFailure failure = FailureOf(_delete);
            (failure.Code, failure.Category)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetFenced,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired
                    )
                );
        }

        [Test]
        public void It_reads_facts_then_inspects_the_selected_target_and_nothing_more()
        {
            foreach (Harness harness in new[] { _protected, _fenced })
            {
                harness
                    .Engine.Calls.Should()
                    .Equal(
                        new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.AcquireTargetLock, "EdFi_Ods_Test"),
                        new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.ReadServerFacts, null),
                        new SpyDmsTargetEngine.Call(SpyDmsTargetEngine.Inspect, "EdFi_Ods_Test")
                    );
            }
        }

        [Test]
        public void It_releases_the_lock_on_both_paths() =>
            (_protected.OnlyHolder.ReleaseCount, _fenced.OnlyHolder.ReleaseCount).Should().Be((1, 1));
    }

    [TestFixture]
    public class Given_a_delete_finds_the_target_absent
    {
        private Harness _absentDelete = null!;
        private DmsDataStoreDeleteResult _deleteOnAbsent = null!;

        [SetUp]
        public async Task Setup()
        {
            _absentDelete = new Harness();
            _absentDelete.Engine.OnInspect = _ =>
                Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            _deleteOnAbsent = await _absentDelete.Service.DeleteAsync(
                DeleteRequest(),
                CancellationToken.None
            );
        }

        [Test]
        public void It_reports_an_absent_target_as_already_absent_on_delete() =>
            _deleteOnAbsent.Should().BeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();

        [Test]
        public void It_releases_the_lock_after_already_absent() =>
            _absentDelete.OnlyHolder.ReleaseCount.Should().Be(1);

        [Test]
        public void It_issues_no_mutation() =>
            _absentDelete
                .Engine.Members.Should()
                .Equal(
                    SpyDmsTargetEngine.AcquireTargetLock,
                    SpyDmsTargetEngine.ReadServerFacts,
                    SpyDmsTargetEngine.Inspect
                );
    }

    [TestFixture]
    public class Given_the_holder_is_lost_during_the_preamble
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness();
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Engine.OnReadServerFacts = async token =>
            {
                readStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return SpyDmsTargetEngine.Facts();
            };

            Task<DmsDataStoreCreateResult> create = _harness.Service.CreateAsync(
                CreateRequest(),
                CancellationToken.None
            );
            await ReadStartedAsync(readStarted, create);
            _harness.OnlyHolder.Lose();
            _result = await create;
        }

        [Test]
        public void It_ends_the_operation_as_transient_lock_lost_at_the_step_in_flight()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.ReadServerFactsStep
                    )
                );
        }

        [Test]
        public void It_issues_no_engine_call_after_the_loss() =>
            _harness
                .Engine.Members.Should()
                .Equal(SpyDmsTargetEngine.AcquireTargetLock, SpyDmsTargetEngine.ReadServerFacts);

        [Test]
        public void It_still_releases_the_holder() => _harness.OnlyHolder.ReleaseCount.Should().Be(1);
    }

    /// <summary>
    /// Loss is declared by the fake holder while a read is in flight, and the read ignores its token: the
    /// facts read completes, the inspection reports the target absent, or the inspection throws. A fourth
    /// holder is lost before the first read is dispatched. Cancellation never reaches the orchestrator on
    /// these paths; loss must be enforced at the read boundaries themselves.
    /// </summary>
    [TestFixture]
    public class Given_the_holder_is_lost_at_a_read_boundary_without_a_cancellation
    {
        private Harness _beforeFirstRead = null!;
        private Harness _factsCompleted = null!;
        private Harness _inspectReturnedAbsent = null!;
        private Harness _inspectThrew = null!;
        private DmsDataStoreCreateResult _lostBeforeFirstRead = null!;
        private DmsDataStoreCreateResult _lostDuringFacts = null!;
        private DmsDataStoreDeleteResult _lostDuringInspect = null!;
        private DmsDataStoreCreateResult _lostWithException = null!;

        [SetUp]
        public async Task Setup()
        {
            _beforeFirstRead = new Harness();
            Func<CancellationToken, Task<DmsTargetLockResult>> acquire = _beforeFirstRead
                .Engine
                .OnAcquireTargetLock;
            _beforeFirstRead.Engine.OnAcquireTargetLock = async token =>
            {
                DmsTargetLockResult acquired = await acquire(token);
                _beforeFirstRead.OnlyHolder.Lose();
                return acquired;
            };
            _lostBeforeFirstRead = await _beforeFirstRead.Service.CreateAsync(
                CreateRequest(),
                CancellationToken.None
            );

            _factsCompleted = new Harness();
            _factsCompleted.Engine.OnReadServerFacts = _ =>
            {
                _factsCompleted.OnlyHolder.Lose();
                return Task.FromResult(SpyDmsTargetEngine.Facts());
            };
            _lostDuringFacts = await _factsCompleted.Service.CreateAsync(
                CreateRequest(),
                CancellationToken.None
            );

            _inspectReturnedAbsent = new Harness();
            _inspectReturnedAbsent.Engine.OnInspect = _ =>
            {
                _inspectReturnedAbsent.OnlyHolder.Lose();
                return Task.FromResult<DmsLiveTargetState>(new DmsLiveTargetState.Absent());
            };
            _lostDuringInspect = await _inspectReturnedAbsent.Service.DeleteAsync(
                DeleteRequest(),
                CancellationToken.None
            );

            _inspectThrew = new Harness();
            _inspectThrew.Engine.OnInspect = _ =>
            {
                _inspectThrew.OnlyHolder.Lose();
                throw new InvalidOperationException("server said: password=hunter2", new TimeoutException());
            };
            _lostWithException = await _inspectThrew.Service.CreateAsync(
                CreateRequest(),
                CancellationToken.None
            );
        }

        [Test]
        public void It_ends_every_path_as_transient_lock_lost_at_the_step_in_flight()
        {
            foreach (
                (DmsProvisioningFailure failure, string step) in new[]
                {
                    (FailureOf(_lostBeforeFirstRead), DmsDataStoreProvisioningService.ReadServerFactsStep),
                    (FailureOf(_lostDuringFacts), DmsDataStoreProvisioningService.ReadServerFactsStep),
                    (FailureOf(_lostDuringInspect), DmsDataStoreProvisioningService.InspectTargetStep),
                    (FailureOf(_lostWithException), DmsDataStoreProvisioningService.InspectTargetStep),
                }
            )
            {
                (failure.Code, failure.Category, failure.Diagnostic.Operation)
                    .Should()
                    .Be(
                        (DmsProvisioningFailureCode.LockLost, DmsProvisioningFailureCategory.Transient, step)
                    );
            }
        }

        [Test]
        public void It_does_not_trust_the_inspection_that_completed_after_the_loss() =>
            _lostDuringInspect.Should().NotBeOfType<DmsDataStoreDeleteResult.AlreadyAbsent>();

        [Test]
        public void It_dispatches_no_read_after_the_loss()
        {
            _beforeFirstRead.Engine.Members.Should().Equal(SpyDmsTargetEngine.AcquireTargetLock);
            _factsCompleted
                .Engine.Members.Should()
                .Equal(SpyDmsTargetEngine.AcquireTargetLock, SpyDmsTargetEngine.ReadServerFacts);
            foreach (Harness harness in new[] { _inspectReturnedAbsent, _inspectThrew })
            {
                harness
                    .Engine.Members.Should()
                    .Equal(
                        SpyDmsTargetEngine.AcquireTargetLock,
                        SpyDmsTargetEngine.ReadServerFacts,
                        SpyDmsTargetEngine.Inspect
                    );
            }
        }

        [Test]
        public void It_keeps_nothing_of_the_exception_that_accompanied_the_loss()
        {
            DmsProvisioningDiagnostic diagnostic = FailureOf(_lostWithException).Diagnostic;
            (diagnostic.ExceptionTypeChain, diagnostic.ProviderErrorCode).Should().Be((string.Empty, null));
        }

        [Test]
        public void It_releases_every_holder() =>
            (
                _beforeFirstRead.OnlyHolder.ReleaseCount,
                _factsCompleted.OnlyHolder.ReleaseCount,
                _inspectReturnedAbsent.OnlyHolder.ReleaseCount,
                _inspectThrew.OnlyHolder.ReleaseCount
            )
                .Should()
                .Be((1, 1, 1, 1));
    }

    [TestFixture]
    public class Given_a_preamble_read_exceeds_the_command_timeout
    {
        private Harness _harness = null!;
        private DmsDataStoreDeleteResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness();
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Engine.OnInspect = async token =>
            {
                readStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new DmsLiveTargetState.Absent();
            };

            Task<DmsDataStoreDeleteResult> delete = _harness.Service.DeleteAsync(
                DeleteRequest(),
                CancellationToken.None
            );
            await ReadStartedAsync(readStarted, delete);
            _harness.Time.Advance(CommandTimeout);
            _result = await delete;
        }

        [Test]
        public void It_reports_timed_out_at_the_step()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TimedOut,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.InspectTargetStep
                    )
                );
        }

        [Test]
        public void It_declares_no_loss_and_releases_the_holder() =>
            (_harness.OnlyHolder.IsLost, _harness.OnlyHolder.ReleaseCount).Should().Be((false, 1));
    }

    [TestFixture]
    public class Given_a_preamble_read_throws
    {
        private Harness _harness = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness();
            _harness.Engine.OnInspect = _ =>
                throw new InvalidOperationException("server said: password=hunter2", new TimeoutException());
            _result = await _harness.Service.CreateAsync(CreateRequest(), CancellationToken.None);
        }

        [Test]
        public void It_reports_unknown_with_only_the_type_chain()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category)
                .Should()
                .Be((DmsProvisioningFailureCode.Unknown, DmsProvisioningFailureCategory.Transient));
            failure
                .Diagnostic.ExceptionTypeChain.Should()
                .Be("System.InvalidOperationException/System.TimeoutException");
            failure.Diagnostic.Operation.Should().Be(DmsDataStoreProvisioningService.InspectTargetStep);
        }

        [Test]
        public void It_releases_the_holder() => _harness.OnlyHolder.ReleaseCount.Should().Be(1);
    }

    [TestFixture]
    public class Given_the_caller_cancels_during_the_preamble
    {
        private Harness _harness = null!;
        private Func<Task> _create = () => Task.CompletedTask;

        [SetUp]
        public async Task Setup()
        {
            _harness = new Harness();
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Engine.OnReadServerFacts = async token =>
            {
                readStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return SpyDmsTargetEngine.Facts();
            };

            using CancellationTokenSource cancellation = new();
            Task<DmsDataStoreCreateResult> create = _harness.Service.CreateAsync(
                CreateRequest(),
                cancellation.Token
            );
            await ReadStartedAsync(readStarted, create);
            await cancellation.CancelAsync();
            _create = () => create;
            try
            {
                await create;
            }
            catch (OperationCanceledException)
            {
                // Expected; asserted below.
            }
        }

        [Test]
        public async Task It_propagates_the_cancellation() =>
            await _create.Should().ThrowAsync<OperationCanceledException>();

        [Test]
        public void It_releases_the_holder_first() =>
            (_harness.OnlyHolder.ReleaseCount, _harness.OnlyHolder.IsLost).Should().Be((1, false));

        [Test]
        public void It_issues_no_engine_call_after_the_cancellation() =>
            _harness
                .Engine.Members.Should()
                .Equal(SpyDmsTargetEngine.AcquireTargetLock, SpyDmsTargetEngine.ReadServerFacts);
    }
}
