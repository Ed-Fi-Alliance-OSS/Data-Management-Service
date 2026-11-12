// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.TargetFactsSample;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>
/// The orchestrator's reconcile (6.5, Step 3.7) of a target the preamble finds present, over the spy engine
/// and the real catalog: reads only, never a staging create, a restore, a rename, a marker, or a drop; an
/// owned target is <c>Reconciled</c> only after every 6.9c check passes against the trusted manifest, so a
/// matching identity with drifted content, engine, inventory, ownership, or privileges is refused for
/// inspection; another identity, an unreadable one, a refused session, or a database that is not online
/// stops after the identity read; a pin that differs from the staged artifact stops before any server call;
/// loss and the caller's cancellation end the reads with nothing mutated.
/// </summary>
public class DmsDataStoreProvisioningServiceReconcileTests
{
    private static readonly Guid _expected = Guid.Parse("2b3c4d5e-6f70-4182-93a4-b5c6d7e8f901");
    private static readonly DmsInstanceIdentity.Postgresql _targetInstance = new(16385, "");

    private static readonly DmsDataStoreCreateRequest _request = CreateRequest(
        expectedSourceIdentity: _expected
    );

    private static readonly string[] _reads =
    [
        SpyDmsTargetEngine.AcquireTargetLock,
        SpyDmsTargetEngine.ReadServerFacts,
        SpyDmsTargetEngine.Inspect,
        SpyDmsTargetEngine.OpenSession,
        SpyDmsTargetEngine.ReadIdentity,
        SpyDmsTargetEngine.ReadTargetFacts,
    ];

    private sealed class FakeSession(DmsInstanceIdentity instance) : DmsSession(instance, Session)
    {
        private int _disposals;

        public int Disposals => _disposals;

        public override ValueTask DisposeAsync()
        {
            _disposals++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A harness whose target is present and online under <see cref="_targetInstance"/>, whose session reads
    /// the expected identity and the facts the shared catalog's manifest describes, and which records every
    /// session it opens.
    /// </summary>
    private sealed class PresentTarget
    {
        public PresentTarget(
            DmsLiveTargetState? state = null,
            Action<DmsDataStoreProvisioningOptions>? configure = null
        )
        {
            Harness = new Harness(configure: configure);
            Harness.Engine.OnInspectByName = (_, _) =>
                Task.FromResult(
                    state ?? new DmsLiveTargetState.Present(_targetInstance, DmsDatabaseState.Online)
                );
            Harness.Engine.OnOpenSession = (_, instance, _) =>
            {
                FakeSession session = new(instance);
                Sessions.Add(session);
                return Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.Opened(session));
            };
            Harness.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(_expected));
            Harness.Engine.OnReadTargetFacts = (_, _) => Task.FromResult(PostgresqlFacts());
        }

        public Harness Harness { get; }

        public List<FakeSession> Sessions { get; } = [];

        public Task<DmsDataStoreCreateResult> CreateAsync(
            DmsDataStoreCreateRequest? request = null,
            CancellationToken cancellationToken = default
        ) => Harness.Service.CreateAsync(request ?? _request, cancellationToken);
    }

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsDataStoreCreateResult result
    )
    {
        DmsProvisioningFailure failure = FailureOf(result);
        return (failure.Code, failure.Category, failure.Diagnostic.Operation);
    }

    [TestFixture]
    public class Given_the_preamble_finds_the_target_carrying_the_expected_identity
    {
        private PresentTarget _owned = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _owned = new PresentTarget();
            _result = await _owned.CreateAsync();
        }

        [Test]
        public void It_reconciles_with_the_pin_and_no_residue() =>
            _result
                .Should()
                .Be(
                    new DmsDataStoreCreateResult.Reconciled(SharedTemplateCatalog.Pin, DmsCleanupReport.Clean)
                );

        [Test]
        public void It_only_reads_the_identity_and_the_full_facts() =>
            _owned.Harness.Engine.Members.Should().Equal(_reads);

        [Test]
        public void It_reads_on_a_session_bound_to_the_inspected_instance_and_closes_it() =>
            _owned
                .Sessions.Should()
                .ContainSingle()
                .Which.Should()
                .Match<FakeSession>(session => session.Instance == _targetInstance && session.Disposals == 1);

        [Test]
        public void It_addresses_the_target_by_its_name() =>
            _owned
                .Harness.Engine.Calls.Single(call => call.Member == SpyDmsTargetEngine.OpenSession)
                .DatabaseName.Should()
                .Be(_request.TargetName.Value);

        [Test]
        public void It_releases_the_lock() => _owned.Harness.OnlyHolder.ReleaseCount.Should().Be(1);
    }

    [TestFixture]
    public class Given_the_target_carries_the_expected_identity_but_fails_the_full_validation
    {
        private static readonly (
            string Check,
            Func<DmsTargetFacts, DmsTargetFacts> Drift,
            DmsProvisioningFailureCode Code
        )[] _table =
        [
            (
                DmsTargetValidator.EffectiveSchemaCheck,
                facts =>
                    facts with
                    {
                        EffectiveSchema = new DmsEffectiveSchemaRead.Found(
                            "1.0.0",
                            new string('0', 64),
                            42,
                            ResourceKeySeedHashB64
                        ),
                    },
                DmsProvisioningFailureCode.TargetIncompatible
            ),
            (
                DmsTargetValidator.EngineMajorCheck,
                facts => facts with { EngineMajorVersion = 17 },
                DmsProvisioningFailureCode.TargetIncompatible
            ),
            (
                DmsTargetValidator.InventoryHashCheck,
                facts => facts.WithObjects(new DmsInventoryObject("edfi", "Student", "table")),
                DmsProvisioningFailureCode.TargetIncompatible
            ),
            (
                DmsTargetValidator.OwnershipCheck,
                facts => facts.WithOwnership(Owned("relation", "intruder", "edfi.\"School\"")),
                DmsProvisioningFailureCode.SecurityStateViolation
            ),
            (
                DmsTargetValidator.PrivilegesCheck,
                facts =>
                    facts.WithPrivileges(Held("relation", "SELECT", Admin, "intruder", "edfi.\"School\"")),
                DmsProvisioningFailureCode.SecurityStateViolation
            ),
        ];

        private readonly List<(DmsDataStoreCreateResult Result, PresentTarget Owned)> _runs = [];

        [SetUp]
        public async Task Setup()
        {
            _runs.Clear();
            foreach ((_, Func<DmsTargetFacts, DmsTargetFacts> drift, _) in _table)
            {
                PresentTarget owned = new();
                owned.Harness.Engine.OnReadTargetFacts = (_, _) => Task.FromResult(drift(PostgresqlFacts()));
                _runs.Add((await owned.CreateAsync(), owned));
            }
        }

        [Test]
        public void It_refuses_each_drift_for_inspection_at_its_check() =>
            _runs
                .Select(run => Verdict(run.Result))
                .Should()
                .Equal(
                    _table.Select(row =>
                        (
                            row.Code,
                            DmsProvisioningFailureCategory.OperatorInspectionRequired,
                            DmsDataStoreProvisioningService.ReconcileValidateStep + "." + row.Check
                        )
                    )
                );

        [Test]
        public void It_mutates_nothing_and_closes_the_session() =>
            _runs
                .Should()
                .AllSatisfy(run =>
                {
                    run.Owned.Harness.Engine.Members.Should().Equal(_reads);
                    run.Owned.Sessions.Should().ContainSingle().Which.Disposals.Should().Be(1);
                });
    }

    [TestFixture]
    public class Given_the_target_is_not_owned_or_cannot_be_verified
    {
        private PresentTarget _foreign = null!;
        private PresentTarget _unreadable = null!;
        private PresentTarget _refused = null!;
        private PresentTarget _offline = null!;
        private DmsDataStoreCreateResult _foreignResult = null!;
        private DmsDataStoreCreateResult _unreadableResult = null!;
        private DmsDataStoreCreateResult _refusedResult = null!;
        private DmsDataStoreCreateResult _offlineResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _foreign = new PresentTarget();
            _foreign.Harness.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(new DmsIdentityRead.Found(PackageIdentity));
            _foreignResult = await _foreign.CreateAsync();

            _unreadable = new PresentTarget();
            _unreadable.Harness.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(
                    new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing)
                );
            _unreadableResult = await _unreadable.CreateAsync();

            _refused = new PresentTarget();
            _refused.Harness.Engine.OnOpenSession = (_, _, _) =>
                Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.InstanceChanged());
            _refusedResult = await _refused.CreateAsync();

            _offline = new PresentTarget(
                new DmsLiveTargetState.Present(_targetInstance, DmsDatabaseState.Other)
            );
            _offlineResult = await _offline.CreateAsync();
        }

        [Test]
        public void It_reports_another_identity_as_unowned() =>
            Verdict(_foreignResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_reports_an_unreadable_identity_as_unverifiable() =>
            Verdict(_unreadableResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_reports_a_refused_session_as_unverifiable() =>
            Verdict(_refusedResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileOpenSessionStep
                    )
                );

        [Test]
        public void It_reports_a_database_that_is_not_online_as_unverifiable_without_a_session() =>
            (
                Verdict(_offlineResult),
                _offline.Harness.Engine.Members.Contains(SpyDmsTargetEngine.OpenSession)
            )
                .Should()
                .Be(
                    (
                        (
                            DmsProvisioningFailureCode.TargetUnverifiable,
                            DmsProvisioningFailureCategory.OperatorInspectionRequired,
                            DmsDataStoreProvisioningService.InspectTargetStep
                        ),
                        false
                    )
                );

        [Test]
        public void It_never_reads_the_facts_of_a_target_it_does_not_own()
        {
            foreach (PresentTarget owned in new[] { _foreign, _unreadable, _refused, _offline })
            {
                owned.Harness.Engine.Members.Should().BeSubsetOf(_reads);
                owned.Harness.Engine.Members.Should().NotContain(SpyDmsTargetEngine.ReadTargetFacts);
            }
        }
    }

    [TestFixture]
    public class Given_the_server_refuses_the_admin_access_to_the_target
    {
        private static readonly DmsProvisioningDiagnostic _refusal = new(
            "Npgsql.PostgresException",
            "42501",
            "EngineOperation"
        );

        private PresentTarget _identityDenied = null!;
        private PresentTarget _connectDenied = null!;
        private PresentTarget _factsDenied = null!;
        private PresentTarget _factsDeniedAlongsideLoss = null!;
        private DmsDataStoreCreateResult _identityResult = null!;
        private DmsDataStoreCreateResult _connectResult = null!;
        private DmsDataStoreCreateResult _factsResult = null!;
        private DmsDataStoreCreateResult _factsAlongsideLossResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _identityDenied = new PresentTarget();
            _identityDenied.Harness.Engine.OnReadIdentity = (_, _) =>
                Task.FromResult<DmsIdentityRead>(
                    new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied) { Diagnostic = _refusal }
                );
            _identityResult = await _identityDenied.CreateAsync();

            _connectDenied = new PresentTarget();
            _connectDenied.Harness.Engine.OnOpenSession = (_, _, _) =>
                Task.FromResult<DmsSessionOpenResult>(new DmsSessionOpenResult.AccessDenied(_refusal));
            _connectResult = await _connectDenied.CreateAsync();

            // The identity matches; the facts read is refused (for example on dms.EffectiveSchema).
            _factsDenied = new PresentTarget();
            _factsDenied.Harness.Engine.OnReadTargetFacts = (_, _) =>
                throw new DmsAccessDeniedException(_refusal);
            _factsResult = await _factsDenied.CreateAsync();

            // The same refusal raised while the holder is lost is loss, not a refusal.
            _factsDeniedAlongsideLoss = new PresentTarget();
            _factsDeniedAlongsideLoss.Harness.Engine.OnReadTargetFacts = (_, _) =>
            {
                _factsDeniedAlongsideLoss.Harness.OnlyHolder.Lose();
                throw new DmsAccessDeniedException(_refusal);
            };
            _factsAlongsideLossResult = await _factsDeniedAlongsideLoss.CreateAsync();
        }

        [Test]
        public void It_reports_a_refused_identity_read_as_unverifiable_with_the_sanitized_diagnostic() =>
            FailureOf(_identityResult)
                .Should()
                .Be(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            "42501",
                            DmsDataStoreProvisioningService.ReconcileIdentityStep
                        )
                    )
                );

        [Test]
        public void It_reports_a_refused_connection_as_unverifiable_with_the_sanitized_diagnostic() =>
            FailureOf(_connectResult)
                .Should()
                .Be(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            "42501",
                            DmsDataStoreProvisioningService.ReconcileOpenSessionStep
                        )
                    )
                );

        [Test]
        public void It_reads_nothing_after_the_refusal_and_releases_what_it_opened()
        {
            _identityDenied.Harness.Engine.Members.Should().Equal(_reads[..^1]);
            _connectDenied.Harness.Engine.Members.Should().Equal(_reads[..^2]);
            _factsDenied.Harness.Engine.Members.Should().Equal(_reads);
            (
                _identityDenied.Sessions.Single().Disposals,
                _connectDenied.Sessions.Count,
                _factsDenied.Sessions.Single().Disposals,
                _identityDenied.Harness.OnlyHolder.ReleaseCount,
                _connectDenied.Harness.OnlyHolder.ReleaseCount,
                _factsDenied.Harness.OnlyHolder.ReleaseCount
            )
                .Should()
                .Be((1, 0, 1, 1, 1, 1));
        }

        [Test]
        public void It_reports_a_refused_facts_read_as_unverifiable_with_the_sanitized_diagnostic() =>
            FailureOf(_factsResult)
                .Should()
                .Be(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        new DmsProvisioningDiagnostic(
                            "Npgsql.PostgresException",
                            "42501",
                            DmsDataStoreProvisioningService.ReconcileReadTargetFactsStep
                        )
                    )
                );

        [Test]
        public void It_keeps_a_refusal_raised_alongside_loss_as_transient_lock_lost() =>
            Verdict(_factsAlongsideLossResult)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.ReconcileReadTargetFactsStep
                    )
                );
    }

    [TestFixture]
    public class Given_the_deployment_expects_another_project_set
    {
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            // The target matches the manifest; the deployment's Compatibility:Projects is the authority it is
            // also judged against (6.9c item 6).
            PresentTarget owned = new(configure: options =>
                options.Compatibility!.Projects = [.. Projects, "tpdm"]
            );
            _result = await owned.CreateAsync();
        }

        [Test]
        public void It_refuses_the_target_at_the_project_set() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileValidateStep
                            + "."
                            + DmsTargetValidator.ProjectSetCheck
                    )
                );
    }

    [TestFixture]
    public class Given_a_retry_whose_pin_differs_from_the_staged_artifact
    {
        private PresentTarget _owned = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _owned = new PresentTarget();
            _result = await _owned.CreateAsync(_request with { PinnedArtifact = Pin("e") });
        }

        [Test]
        public void It_refuses_before_any_server_call()
        {
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveArtifactStep
                    )
                );
            _owned.Harness.Engine.Calls.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_the_holder_is_lost_while_the_facts_are_read
    {
        private PresentTarget _owned = null!;
        private DmsDataStoreCreateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _owned = new PresentTarget();
            _owned.Harness.Engine.OnReadTargetFacts = (_, _) =>
            {
                _owned.Harness.OnlyHolder.Lose();
                return Task.FromResult(PostgresqlFacts());
            };
            _result = await _owned.CreateAsync();
        }

        [Test]
        public void It_ends_as_transient_lock_lost_because_nothing_was_mutated() =>
            Verdict(_result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.LockLost,
                        DmsProvisioningFailureCategory.Transient,
                        DmsDataStoreProvisioningService.ReconcileReadTargetFactsStep
                    )
                );

        [Test]
        public void It_closes_the_session_and_releases_the_lock() =>
            (_owned.Sessions.Single().Disposals, _owned.Harness.OnlyHolder.ReleaseCount).Should().Be((1, 1));
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_the_identity_is_read
    {
        private PresentTarget _owned = null!;
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            _owned = new PresentTarget();
            TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _owned.Harness.Engine.OnReadIdentity = async (_, token) =>
            {
                readStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new DmsIdentityRead.Found(_expected);
            };
            using CancellationTokenSource caller = new();
            Task<DmsDataStoreCreateResult> create = _owned.CreateAsync(cancellationToken: caller.Token);
            await ReadStartedAsync(readStarted, create);
            await caller.CancelAsync();
            _outcome = await Record.ExceptionAsync(() => create);
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _outcome.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_mutates_nothing_closes_the_session_and_releases_the_lock()
        {
            _owned.Harness.Engine.Members.Should().Equal(_reads[..^1]);
            (_owned.Sessions.Single().Disposals, _owned.Harness.OnlyHolder.ReleaseCount).Should().Be((1, 1));
        }
    }
}
