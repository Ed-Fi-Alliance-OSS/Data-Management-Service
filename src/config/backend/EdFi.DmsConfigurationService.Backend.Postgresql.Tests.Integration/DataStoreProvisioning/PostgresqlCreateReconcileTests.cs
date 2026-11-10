// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The orchestrator's reconcile end to end (6.5, Step 3.7) over the real catalog and engine with a real
/// <c>psql</c>: a target created by this provisioner is provisioned again. With its pin and expected identity
/// it is <c>Reconciled</c> after the full validation; with another expected identity it is
/// <c>TargetUnowned</c>; without a pin the request is refused before any server call; after the allowlist
/// moves to another package with identical content, the old pin is <c>ArtifactChangedSinceCreate</c> before
/// any server call. A target whose effective schema was corrupted, or which was granted a privilege after the
/// create, carries the expected identity and is still refused. No retry mutates the target: its instance,
/// identity, sentinel document, object count and canonical inventory hash are unchanged, and the engine
/// recorded nothing but reads.
/// </summary>
[Category(Category)]
public class PostgresqlCreateReconcileTests
{
    private static readonly Guid _sentinelDocument = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");

    /// <summary>Every engine member a reconcile may call; anything else would be a mutation or a staging step.</summary>
    private static readonly string[] _readMembers =
    [
        nameof(IDmsTargetEngine.AcquireTargetLockAsync),
        nameof(IDmsTargetEngine.ReadServerFactsAsync),
        nameof(IDmsTargetEngine.InspectAsync),
        nameof(IDmsTargetEngine.OpenSessionAsync),
        nameof(IDmsTargetEngine.ReadIdentityAsync),
        nameof(IDmsTargetEngine.ReadTargetFactsAsync),
    ];

    private static void RequireLane()
    {
        RequireConfiguredOrSkip();
        RequirePsqlOrSkip();
    }

    private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string) Verdict(
        DmsDataStoreCreateResult result
    )
    {
        DmsProvisioningFailure failure = result
            .Should()
            .BeOfType<DmsDataStoreCreateResult.Failure>()
            .Subject.Detail;
        return (failure.Code, failure.Category, failure.Diagnostic.Operation);
    }

    /// <summary>What a retry must leave unchanged about the created target.</summary>
    private sealed record Evidence(
        DmsInstanceIdentity.Postgresql Instance,
        Guid Identity,
        bool SentinelDocument,
        long ObjectCount,
        string Inventory
    );

    private static async Task<Evidence> EvidenceAsync(DmsTargetDatabaseName target)
    {
        TestDatabase database = new(target.Value);
        DmsInstanceIdentity.Postgresql instance = await database.CatalogInstanceAsync();
        return new Evidence(
            instance,
            await database.ScalarAsync<Guid>("SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";"),
            await database.ScalarAsync<bool>(
                $"SELECT EXISTS (SELECT 1 FROM \"dms\".\"Document\" WHERE \"DocumentUuid\" = '{_sentinelDocument:D}');"
            ),
            await database.ScalarAsync<long>(TargetEvidence.ObjectCountQuery),
            await TargetEvidence.InventoryFingerprintAsync(target, instance)
        );
    }

    /// <summary>A lane whose create has published the target; the fixture's own retries follow.</summary>
    private static async Task<Lane> CreatedAsync()
    {
        Lane lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
        (await lane.CreateAsync()).Should().BeOfType<DmsDataStoreCreateResult.Created>();
        return lane;
    }

    /// <summary>Runs <paramref name="retry"/> and returns its result with the engine calls it made.</summary>
    private static async Task<(DmsDataStoreCreateResult Result, string[] Members)> RetryAsync(
        RecordingEngine engine,
        Func<Task<DmsDataStoreCreateResult>> retry
    )
    {
        int before = engine.Calls.Count;
        DmsDataStoreCreateResult result = await retry();
        return (result, [.. engine.Calls.Skip(before).Select(call => call.Member)]);
    }

    [TestFixture]
    public class Given_a_created_target_is_provisioned_again
    {
        private Lane? _lane;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private (DmsDataStoreCreateResult Result, string[] Members) _withPin;
        private (DmsDataStoreCreateResult Result, string[] Members) _otherIdentity;
        private (DmsDataStoreCreateResult Result, string[] Members) _withoutPin;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireLane();
            await EnsureEnqueueOwnerRoleAsync();
            _lane = await CreatedAsync();

            // Data, not schema: the inventory and the validation are unaffected, the evidence is not.
            await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                $"INSERT INTO \"dms\".\"Document\" (\"DocumentUuid\", \"ResourceKeyId\") VALUES ('{_sentinelDocument:D}', 1);"
            );
            _before = await EvidenceAsync(_lane.Target);

            Lane lane = _lane;
            _withPin = await RetryAsync(lane.Engine, () => lane.Service.CreateAsync(lane.Request, default));
            _otherIdentity = await RetryAsync(
                lane.Engine,
                () =>
                    lane.Service.CreateAsync(
                        lane.Request with
                        {
                            ExpectedSourceIdentity = Guid.NewGuid(),
                        },
                        default
                    )
            );
            _withoutPin = await RetryAsync(
                lane.Engine,
                () => lane.Service.CreateAsync(lane.Request with { PinnedArtifact = null! }, default)
            );
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_reconciles_the_owned_target_with_its_pin() =>
            _withPin
                .Result.Should()
                .Be(
                    new DmsDataStoreCreateResult.Reconciled(
                        _lane!.Request.PinnedArtifact,
                        DmsCleanupReport.Clean
                    )
                );

        [Test]
        public void It_reports_another_expected_identity_as_unowned() =>
            Verdict(_otherIdentity.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnowned,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_refuses_a_retry_without_a_pin_before_any_server_call()
        {
            Verdict(_withoutPin.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactPinMissing,
                        DmsProvisioningFailureCategory.InvalidRequest,
                        DmsDataStoreProvisioningService.BoundaryStep
                    )
                );
            _withoutPin.Members.Should().BeEmpty();
        }

        [Test]
        public void It_only_read_during_the_retries()
        {
            _withPin.Members.Should().Equal(_readMembers);
            _otherIdentity.Members.Should().Equal(_readMembers[..^1]);
        }

        [Test]
        public void It_left_the_target_exactly_as_it_was()
        {
            _before.Should().Match<Evidence>(evidence => evidence.SentinelDocument);
            _after.Should().Be(_before);
            _after.Identity.Should().Be(_lane!.Request.ExpectedSourceIdentity);
        }

        [Test]
        public void It_left_no_operation_directory() => _lane!.OperationEntries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_allowlist_moved_to_another_package_with_identical_content
    {
        private Lane? _lane;
        private Lane.Deployment _switched = null!;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private (DmsDataStoreCreateResult Result, string[] Members) _oldPin;
        private (DmsDataStoreCreateResult Result, string[] Members) _newPin;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireLane();
            await EnsureEnqueueOwnerRoleAsync();
            _lane = await CreatedAsync();
            _before = await EvidenceAsync(_lane.Target);

            // The same schema and inventory, different artifact bytes, a different package version.
            _switched = await _lane.SwitchAllowlistAsync(Lane.ShapedSql("\n-- republished\n"), "1.0.124");
            Lane lane = _lane;
            _oldPin = await RetryAsync(
                _switched.Engine,
                () => _switched.Service.CreateAsync(lane.Request, default)
            );

            // The control: the content passes the full validation, so only the pin tells the packages apart.
            _newPin = await RetryAsync(
                _switched.Engine,
                () =>
                    _switched.Service.CreateAsync(
                        lane.Request with
                        {
                            PinnedArtifact = _switched.Pin,
                        },
                        default
                    )
            );
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }
        }

        [Test]
        public void It_switched_to_a_package_that_differs_only_in_bytes_and_version()
        {
            DmsTrustedArtifactPin created = _lane!.Request.PinnedArtifact;
            (
                _switched.Pin.PackageSha256 == created.PackageSha256,
                _switched.Pin.ArtifactSha256 == created.ArtifactSha256,
                _switched.Pin.PackageVersion == created.PackageVersion
            )
                .Should()
                .Be((false, false, false));
            (_switched.Pin.InventorySha256, _switched.Pin.EffectiveSchemaHash)
                .Should()
                .Be((created.InventorySha256, created.EffectiveSchemaHash));
        }

        [Test]
        public void It_refuses_the_old_pin_before_any_server_call()
        {
            Verdict(_oldPin.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ResolveArtifactStep
                    )
                );
            _oldPin.Members.Should().BeEmpty();
        }

        [Test]
        public void It_would_reconcile_the_identical_content_under_the_new_pin() =>
            (_newPin.Result, _newPin.Members)
                .Should()
                .Match<(DmsDataStoreCreateResult Result, string[] Members)>(run =>
                    run.Result is DmsDataStoreCreateResult.Reconciled
                    && run.Members.SequenceEqual(_readMembers)
                );

        [Test]
        public void It_left_the_target_exactly_as_it_was() => _after.Should().Be(_before);
    }

    [TestFixture]
    public class Given_a_created_target_drifted_after_the_create
    {
        private Lane? _corrupted;
        private Lane? _granted;
        private string? _grantee;
        private (DmsDataStoreCreateResult Result, string[] Members) _corruptedRetry;
        private (DmsDataStoreCreateResult Result, string[] Members) _grantedRetry;
        private (Evidence Before, Evidence After) _corruptedEvidence;
        private (Evidence Before, Evidence After) _grantedEvidence;
        private string _corruptedHashAfter = "";
        private bool _grantHeldAfter;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireLane();
            await EnsureEnqueueOwnerRoleAsync();

            _corrupted = await CreatedAsync();
            TestDatabase corrupted = new(_corrupted.Target.Value);
            await corrupted.ExecuteAsync(
                $"UPDATE \"dms\".\"EffectiveSchema\" SET \"EffectiveSchemaHash\" = '{new string('0', 64)}';"
            );
            Evidence corruptedBefore = await EvidenceAsync(_corrupted.Target);
            Lane corruptedLane = _corrupted;
            _corruptedRetry = await RetryAsync(
                corruptedLane.Engine,
                () => corruptedLane.Service.CreateAsync(corruptedLane.Request, default)
            );
            _corruptedEvidence = (corruptedBefore, await EvidenceAsync(_corrupted.Target));
            _corruptedHashAfter = await corrupted.ScalarAsync<string>(
                "SELECT \"EffectiveSchemaHash\" FROM \"dms\".\"EffectiveSchema\";"
            );

            _granted = await CreatedAsync();
            _grantee = await CreateRoleAsync("reconcile");
            TestDatabase granted = new(_granted.Target.Value);
            await granted.ExecuteAsync(
                $"GRANT SELECT ON \"edfi\".\"School\" TO {PostgresqlIdentifiers.Quote(_grantee)};"
            );
            Evidence grantedBefore = await EvidenceAsync(_granted.Target);
            Lane grantedLane = _granted;
            _grantedRetry = await RetryAsync(
                grantedLane.Engine,
                () => grantedLane.Service.CreateAsync(grantedLane.Request, default)
            );
            _grantedEvidence = (grantedBefore, await EvidenceAsync(_granted.Target));
            _grantHeldAfter = await granted.ScalarAsync<bool>(
                $"SELECT has_table_privilege({PostgresqlIdentifiers.Literal(_grantee)}, '\"edfi\".\"School\"', 'SELECT');"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane? lane in new[] { _corrupted, _granted })
            {
                if (lane is not null)
                {
                    await lane.DisposeAsync();
                }
            }

            if (_grantee is not null)
            {
                await DropRoleAsync(_grantee);
            }
        }

        [Test]
        public void It_refuses_a_corrupted_effective_schema_despite_the_expected_identity() =>
            Verdict(_corruptedRetry.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileValidateStep
                            + "."
                            + DmsTargetValidator.EffectiveSchemaCheck
                    )
                );

        [Test]
        public void It_refuses_a_privilege_granted_after_the_create_despite_the_expected_identity() =>
            Verdict(_grantedRetry.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileValidateStep
                            + "."
                            + DmsTargetValidator.PrivilegesCheck
                    )
                );

        [Test]
        public void It_read_the_full_facts_and_nothing_more() =>
            (_corruptedRetry.Members, _grantedRetry.Members)
                .Should()
                .Match<(string[] Corrupted, string[] Granted)>(members =>
                    members.Corrupted.SequenceEqual(_readMembers)
                    && members.Granted.SequenceEqual(_readMembers)
                );

        [Test]
        public void It_left_both_drifts_in_place_for_inspection()
        {
            _corruptedEvidence.After.Should().Be(_corruptedEvidence.Before);
            _grantedEvidence.After.Should().Be(_grantedEvidence.Before);
            (_corruptedHashAfter, _grantHeldAfter).Should().Be((new string('0', 64), true));
        }
    }

    /// <summary>
    /// F-01 over the real catalog and engine, without <c>psql</c>: an owned target seeded under the target name
    /// reconciles when intact; with its identity column dropped it is <c>TargetUnverifiable</c>; with a NULL or a
    /// missing <c>dms.EffectiveSchema</c> value, or (R2-1) with Populated-predicate tables whose shape the server
    /// refuses, it is <c>TargetIncompatible</c>, all for inspection, never <c>Unknown</c>. The lane's template is
    /// Minimal, whose object-level inventory does not see the column, so the predicate check alone refuses it; with
    /// both defects the effective schema is reported first. Each retry only reads, and the target keeps its instance
    /// and stays open to connections.
    /// </summary>
    [TestFixture]
    public class Given_an_owned_target_with_malformed_metadata
    {
        private readonly List<Lane> _lanes = [];
        private Run _intact = null!;
        private Run _identityColumnMissing = null!;
        private Run _nullHash = null!;
        private Run _seedHashMissing = null!;
        private Run _resourceNameMissing = null!;
        private Run _integerResourceName = null!;
        private Run _combined = null!;

        private sealed record Run(
            DmsDataStoreCreateResult Result,
            string[] Members,
            (bool Exists, uint Oid, bool AllowsConnections) Before,
            (bool Exists, uint Oid, bool AllowsConnections) After
        );

        private async Task<Run> RetryOnSeededAsync(string? alteration)
        {
            Lane lane = await Lane.BuildAsync(Lane.ShapedSql(), new LaneHooks());
            _lanes.Add(lane);
            await lane.SeedTargetAsync();
            if (alteration is not null)
            {
                await new TestDatabase(lane.Target.Value).ExecuteAsync(alteration);
            }

            (bool, uint, bool) before = await lane.TargetCatalogAsync();
            (DmsDataStoreCreateResult result, string[] members) = await RetryAsync(
                lane.Engine,
                () => lane.Service.CreateAsync(lane.Request, default)
            );
            return new Run(result, members, before, await lane.TargetCatalogAsync());
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _intact = await RetryOnSeededAsync(null);
            _identityColumnMissing = await RetryOnSeededAsync(
                "ALTER TABLE \"dms\".\"DataStoreIdentity\" DROP COLUMN \"SourceIdentity\";"
            );
            _nullHash = await RetryOnSeededAsync(
                "ALTER TABLE \"dms\".\"EffectiveSchema\" ALTER COLUMN \"EffectiveSchemaHash\" DROP NOT NULL; UPDATE \"dms\".\"EffectiveSchema\" SET \"EffectiveSchemaHash\" = NULL;"
            );
            _seedHashMissing = await RetryOnSeededAsync(
                "ALTER TABLE \"dms\".\"EffectiveSchema\" DROP COLUMN \"ResourceKeySeedHash\";"
            );
            _resourceNameMissing = await RetryOnSeededAsync(
                "ALTER TABLE \"dms\".\"ResourceKey\" DROP COLUMN \"ResourceName\";"
            );
            _integerResourceName = await RetryOnSeededAsync(
                "ALTER TABLE \"dms\".\"ResourceKey\" ALTER COLUMN \"ResourceName\" TYPE integer USING \"ResourceKeyId\";"
            );
            _combined = await RetryOnSeededAsync(
                "ALTER TABLE \"dms\".\"ResourceKey\" DROP COLUMN \"ResourceName\"; ALTER TABLE \"dms\".\"EffectiveSchema\" ALTER COLUMN \"EffectiveSchemaHash\" DROP NOT NULL; UPDATE \"dms\".\"EffectiveSchema\" SET \"EffectiveSchemaHash\" = NULL;"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (Lane lane in _lanes)
            {
                await lane.DisposeAsync();
            }
        }

        [Test]
        public void It_reconciles_the_intact_seeded_target() =>
            _intact.Result.Should().BeOfType<DmsDataStoreCreateResult.Reconciled>();

        [Test]
        public void It_reports_a_missing_identity_column_as_unverifiable_for_inspection() =>
            Verdict(_identityColumnMissing.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep
                    )
                );

        [Test]
        public void It_reports_a_null_effective_schema_value_as_incompatible_for_inspection() =>
            Verdict(_nullHash.Result).Should().Be(EffectiveSchemaIncompatible);

        [Test]
        public void It_reports_a_missing_effective_schema_column_as_incompatible_for_inspection() =>
            Verdict(_seedHashMissing.Result).Should().Be(EffectiveSchemaIncompatible);

        [Test]
        public void It_reports_a_minimal_target_with_malformed_predicate_tables_as_incompatible_for_inspection()
        {
            Verdict(_resourceNameMissing.Result).Should().Be(PopulatedPredicateIncompatible);
            Verdict(_integerResourceName.Result).Should().Be(PopulatedPredicateIncompatible);
        }

        [Test]
        public void It_reports_the_effective_schema_first_when_the_predicate_tables_are_malformed_too() =>
            Verdict(_combined.Result).Should().Be(EffectiveSchemaIncompatible);

        [Test]
        public void It_only_read_during_each_retry()
        {
            _identityColumnMissing.Members.Should().Equal(_readMembers[..^1]);
            _nullHash.Members.Should().Equal(_readMembers);
            _seedHashMissing.Members.Should().Equal(_readMembers);
            _resourceNameMissing.Members.Should().Equal(_readMembers);
            _integerResourceName.Members.Should().Equal(_readMembers);
            _combined.Members.Should().Equal(_readMembers);
        }

        [Test]
        public void It_left_each_target_in_place_and_open() =>
            new[]
            {
                _intact,
                _identityColumnMissing,
                _nullHash,
                _seedHashMissing,
                _resourceNameMissing,
                _integerResourceName,
                _combined,
            }
                .Should()
                .AllSatisfy(run =>
                {
                    run.After.Should().Be(run.Before);
                    run.After.AllowsConnections.Should().BeTrue();
                });

        private static (
            DmsProvisioningFailureCode,
            DmsProvisioningFailureCategory,
            string
        ) EffectiveSchemaIncompatible =>
            (
                DmsProvisioningFailureCode.TargetIncompatible,
                DmsProvisioningFailureCategory.OperatorInspectionRequired,
                DmsDataStoreProvisioningService.ReconcileValidateStep
                    + "."
                    + DmsTargetValidator.EffectiveSchemaCheck
            );

        private static (
            DmsProvisioningFailureCode,
            DmsProvisioningFailureCategory,
            string
        ) PopulatedPredicateIncompatible =>
            (
                DmsProvisioningFailureCode.TargetIncompatible,
                DmsProvisioningFailureCategory.OperatorInspectionRequired,
                DmsDataStoreProvisioningService.ReconcileValidateStep
                    + "."
                    + DmsTargetValidator.PopulatedPredicateCheck
            );
    }

    [TestFixture]
    public class Given_the_admin_role_is_refused_access_to_a_created_target
    {
        private Lane? _lane;
        private string? _role;
        private Evidence _before = null!;
        private Evidence _after = null!;
        private (DmsDataStoreCreateResult Result, string[] Members) _identityDenied;
        private (DmsDataStoreCreateResult Result, string[] Members) _factsDenied;
        private (DmsDataStoreCreateResult Result, string[] Members) _connectDenied;
        private bool _roleSessionsGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireLane();
            await EnsureEnqueueOwnerRoleAsync();
            _lane = await CreatedAsync();
            _before = await EvidenceAsync(_lane.Target);

            // A non-superuser login with no grant on the target's objects: CONNECT through PUBLIC, nothing on
            // the dms schema, so the identity read is refused with 42501.
            _role = await CreateLoginRoleAsync("reconcile");
            Lane.Deployment restricted = _lane.WithEngine(EngineAs(_role));
            DmsDataStoreCreateRequest request = _lane.Request;
            _identityDenied = await RetryAsync(
                restricted.Engine,
                () => restricted.Service.CreateAsync(request, default)
            );

            // The matching identity becomes readable, dms.EffectiveSchema stays refused: the facts read is.
            string grantee = PostgresqlIdentifiers.Quote(_role);
            await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                $"GRANT USAGE ON SCHEMA \"dms\" TO {grantee}; GRANT SELECT ON \"dms\".\"DataStoreIdentity\" TO {grantee};"
            );
            _factsDenied = await RetryAsync(
                restricted.Engine,
                () => restricted.Service.CreateAsync(request, default)
            );

            // CONNECT revoked from PUBLIC: the session open is refused with 42501.
            string quoted = PostgresqlIdentifiers.Quote(_lane.Target.Value);
            await ExecuteOnServerAsync($"REVOKE CONNECT ON DATABASE {quoted} FROM PUBLIC;");
            try
            {
                _connectDenied = await RetryAsync(
                    restricted.Engine,
                    () => restricted.Service.CreateAsync(request, default)
                );
            }
            finally
            {
                await ExecuteOnServerAsync($"GRANT CONNECT ON DATABASE {quoted} TO PUBLIC;");
            }

            string role = _role;
            _roleSessionsGone = await WaitUntilAsync(
                async () =>
                    await ScalarOnServerAsync<long>(
                        "SELECT COUNT(*) FROM pg_catalog.pg_stat_activity WHERE usename = @role;",
                        ("role", role)
                    ) == 0,
                TimeSpan.FromSeconds(20)
            );
            _after = await EvidenceAsync(_lane.Target);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_lane is not null)
            {
                await _lane.DisposeAsync();
            }

            if (_role is not null)
            {
                await DropRoleAsync(_role);
            }
        }

        private static (DmsProvisioningFailureCode, DmsProvisioningFailureCategory, string, string?) Refusal(
            DmsDataStoreCreateResult result
        )
        {
            DmsProvisioningFailure failure = result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            return (
                failure.Code,
                failure.Category,
                failure.Diagnostic.Operation,
                failure.Diagnostic.ProviderErrorCode
            );
        }

        [Test]
        public void It_reports_a_refused_identity_read_as_unverifiable_for_inspection() =>
            Refusal(_identityDenied.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileIdentityStep,
                        "42501"
                    )
                );

        [Test]
        public void It_reports_a_refused_connection_as_unverifiable_for_inspection() =>
            Refusal(_connectDenied.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileOpenSessionStep,
                        "42501"
                    )
                );

        [Test]
        public void It_reads_nothing_after_the_refusal()
        {
            _identityDenied.Members.Should().Equal(_readMembers[..^1]);
            _factsDenied.Members.Should().Equal(_readMembers);
            _connectDenied.Members.Should().Equal(_readMembers[..^2]);
        }

        [Test]
        public void It_reports_a_refused_facts_read_of_an_owned_target_as_unverifiable_for_inspection() =>
            Refusal(_factsDenied.Result)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetUnverifiable,
                        DmsProvisioningFailureCategory.OperatorInspectionRequired,
                        DmsDataStoreProvisioningService.ReconcileReadTargetFactsStep,
                        "42501"
                    )
                );

        [Test]
        public void It_keeps_only_the_type_chain_in_the_diagnostic() =>
            new[] { _identityDenied.Result, _factsDenied.Result, _connectDenied.Result }
                .Select(result =>
                    ((DmsDataStoreCreateResult.Failure)result).Detail.Diagnostic.ExceptionTypeChain
                )
                .Should()
                .AllSatisfy(chain => chain.Should().Be(typeof(Npgsql.PostgresException).FullName));

        [Test]
        public void It_released_every_session_of_the_refused_role_and_its_operation_directories() =>
            (_roleSessionsGone, _lane!.OperationEntries.Length).Should().Be((true, 0));

        [Test]
        public void It_left_the_target_exactly_as_it_was() => _after.Should().Be(_before);
    }
}
