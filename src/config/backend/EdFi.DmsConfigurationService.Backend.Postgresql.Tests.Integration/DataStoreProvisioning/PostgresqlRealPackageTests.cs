// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Step 5.1 on PostgreSQL: the real feed packages in <c>DMS_TEMPLATE_PACKAGE_DIRECTORY</c>, signed with an ephemeral
/// key and allowlisted through the real catalog, replayed by a real <c>psql</c>. Minimal and Sample each create a target
/// that carries the expected identity, the manifest's effective schema and canonical inventory, and passes every
/// validation gate including security; a second create reconciles without mutating; a delete drops it. A database cloned
/// from the operation's own staging database (the same artifact, another identity) takes the name before the rename and
/// is refused untouched. A caller cancellation during the replay and a restore bound below the real replay both drop the
/// staging database. A tampered package and a package for the other engine are refused before any engine call.
/// </summary>
[Category(Category)]
public class PostgresqlRealPackageTests
{
    private static readonly Guid _foreignIdentity = Guid.Parse("5d4c3b2a-1f0e-4d9c-8b7a-6f5e4d3c2b1a");

    private static RealTemplatePackage RequireRealPackage(string databaseEngine, string templateKind)
    {
        RequireConfiguredOrSkip();
        RequirePsqlOrSkip();
        if (!RealTemplatePackage.IsConfigured)
        {
            Assert.Ignore(
                $"{RealTemplatePackage.DirectoryVariable} is not set; the real-package fixtures are skipped."
            );
        }

        return RealTemplatePackage.Find(databaseEngine, templateKind);
    }

    private static RealTemplatePackage RequireRealPackage(string templateKind) =>
        RequireRealPackage(RestoreManifest.PostgresqlEngine, templateKind);

    private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

    private static Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name) =>
        DmsProvisioningTestServer.Engine().InspectAsync(name, default);

    [TestFixture(RestoreManifest.MinimalTemplateKind)]
    [TestFixture(RestoreManifest.PopulatedTemplateKind)]
    public class Given_a_real_package_is_created_reconciled_and_deleted(string templateKind)
    {
        private Lane? _lane;
        private RealTemplatePackage _package = null!;
        private DmsDataStoreCreateResult _created = null!;
        private CreatedEvidence _evidence = null!;
        private DmsDataStoreCreateResult _reconciled = null!;
        private string[] _reconcileCalls = [];
        private DmsDataStoreDeleteResult _deleted = null!;
        private DmsLiveTargetState _targetAfterDelete = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        private static DmsDatabaseTemplate TemplateFor(string kind) =>
            kind == RestoreManifest.MinimalTemplateKind
                ? DmsDatabaseTemplate.Minimal
                : DmsDatabaseTemplate.Sample;

        /// <summary>The engine members that change a database; a reconcile calls none of them.</summary>
        private static readonly string[] _mutatingMembers =
        [
            nameof(IDmsTargetEngine.CreateStagingAsync),
            nameof(IDmsTargetEngine.RestoreIntoStagingAsync),
            nameof(IDmsTargetEngine.RenameIntoTargetAsync),
            nameof(IDmsTargetEngine.AssignMarkerAsync),
            nameof(IDmsTargetEngine.FenceAsync),
            nameof(IDmsTargetEngine.DropInstanceAsync),
            nameof(IDmsTargetEngine.DropStagingAsync),
        ];

        /// <summary>What the created target holds, read on side sessions after the create.</summary>
        private sealed record CreatedEvidence(
            Guid Identity,
            string EffectiveSchema,
            string InventorySha256,
            DmsTargetValidation Validation
        );

        private static async Task<CreatedEvidence> CreatedEvidenceAsync(
            Lane lane,
            RealTemplatePackage package
        )
        {
            PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
            DmsInstanceIdentity instance = (await side.InspectAsync(lane.Target, default))
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Subject.Instance;
            DmsTargetFacts facts;
            DmsIdentityRead identity;
            DmsSession session = (await side.OpenSessionAsync(lane.Target, instance, default))
                .Should()
                .BeOfType<DmsSessionOpenResult.Opened>()
                .Subject.Session;
            await using (session)
            {
                facts = await side.ReadTargetFactsAsync(session, default);
                identity = await side.ReadIdentityAsync(session, default);
            }

            string inventory = DmsTargetValidator.TryBuildInventory(
                facts,
                out CanonicalInventory? built,
                out string? defect
            )
                ? CanonicalInventoryHash.Compute(built)
                : "defect:" + defect;
            DmsTargetValidation validation = DmsTargetValidator.Validate(
                facts,
                identity,
                package.Manifest,
                package.Manifest.Projects,
                new DmsTargetValidationRule.Reconcile(lane.Request.ExpectedSourceIdentity)
            );
            string effectiveSchema = await new TestDatabase(lane.Target.Value).ScalarAsync<string>(
                "SELECT \"EffectiveSchemaHash\" || '|' || \"ResourceKeyCount\" || '|' || \"ApiSchemaFormatVersion\" FROM \"dms\".\"EffectiveSchema\";"
            );
            Guid targetIdentity = await lane.TargetIdentityAsync();
            await WaitForNoSessionsOnAsync(lane.Target.Value);
            return new CreatedEvidence(targetIdentity, effectiveSchema, inventory, validation);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            _package = RequireRealPackage(templateKind);
            _lane = await Lane.BuildAsync(_package, TemplateFor(templateKind), new LaneHooks());
            _created = await _lane.CreateAsync();
            _evidence = await CreatedEvidenceAsync(_lane, _package);

            int callsBefore = _lane.Engine.Calls.Count;
            _reconciled = await _lane.CreateAsync();
            _reconcileCalls = [.. _lane.Engine.Calls.Skip(callsBefore).Select(call => call.Member)];

            _deleted = await _lane.DeleteAsync();
            _targetAfterDelete = await InspectAsync(_lane.Target);
            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
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
        public void It_creates_the_target_from_the_allowlisted_package() =>
            _created
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Created>()
                .Which.Artifact.Should()
                .Match<DmsTrustedArtifactPin>(pin =>
                    pin.PackageId == _package.PackageId
                    && pin.PackageVersion == _package.PackageVersion
                    && pin.TemplateKind == templateKind
                    && pin.InventorySha256 == _package.Manifest.InventorySha256
                );

        [Test]
        public void It_marks_the_target_with_the_expected_identity() =>
            _evidence.Identity.Should().Be(_lane!.Request.ExpectedSourceIdentity);

        [Test]
        public void It_carries_the_manifests_effective_schema() =>
            _evidence
                .EffectiveSchema.Should()
                .Be(
                    $"{_package.Manifest.EffectiveSchemaHash}|{_package.Manifest.ResourceKeyCount}|{_package.Manifest.ApiSchemaFormatVersion}"
                );

        [Test]
        public void It_reads_back_the_canonical_inventory_the_producer_hashed() =>
            _evidence.InventorySha256.Should().Be(_package.Manifest.InventorySha256);

        [Test]
        public void It_passes_every_validation_gate_including_security() =>
            _evidence.Validation.Should().Be(new DmsTargetValidation.Valid());

        [Test]
        public void It_reconciles_a_second_create_without_any_mutating_call()
        {
            _reconciled.Should().BeOfType<DmsDataStoreCreateResult.Reconciled>();
            _reconcileCalls.Should().NotBeEmpty().And.NotContain(_mutatingMembers);
        }

        [Test]
        public void It_drops_the_target_on_delete() =>
            (
                _deleted.Should().BeOfType<DmsDataStoreDeleteResult.Dropped>().Subject.Cleanup.HasResidue,
                _targetAfterDelete
            )
                .Should()
                .Be((false, new DmsLiveTargetState.Absent()));

        [Test]
        public void It_leaves_no_staging_database_and_no_operation_entry() =>
            (_stagingAfter, _lane!.OperationEntries.Length).Should().Be((new DmsLiveTargetState.Absent(), 0));
    }

    [TestFixture]
    public class Given_a_database_of_the_same_artifact_takes_the_target_name_before_the_rename
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private ForeignEvidence _before = null!;
        private ForeignEvidence _after = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        private sealed record ForeignEvidence(
            DmsInstanceIdentity.Postgresql Instance,
            Guid Identity,
            string Sentinel,
            long ObjectCount,
            string Inventory
        );

        private static async Task<ForeignEvidence> ForeignEvidenceAsync(DmsTargetDatabaseName target)
        {
            TestDatabase foreign = new(target.Value);
            DmsInstanceIdentity.Postgresql instance = await foreign.CatalogInstanceAsync();
            ForeignEvidence evidence = new(
                instance,
                await foreign.ScalarAsync<Guid>(
                    "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";"
                ),
                await foreign.ScalarAsync<string>(
                    "SELECT string_agg(marker, ',') FROM public.collision_sentinel;"
                ),
                await foreign.ScalarAsync<long>(TargetEvidence.ObjectCountQuery),
                await TargetEvidence.InventoryFingerprintAsync(target, instance)
            );
            await WaitForNoSessionsOnAsync(target.Value);
            return evidence;
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.MinimalTemplateKind);
            _lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, _hooks);
            _hooks.Probe = async (point, operation) =>
            {
                if (point != DmsProvisioningHookPoint.BeforeRename)
                {
                    return;
                }

                // The validated staging database is the restored artifact: a clone of it is the same artifact's
                // content under the target name, given another identity and a sentinel.
                await WaitForNoSessionsOnAsync(operation.StagingDatabaseName.Value);
                await ExecuteOnServerAsync(
                    $"CREATE DATABASE {PostgresqlIdentifiers.Quote(_lane.Target.Value)} TEMPLATE {PostgresqlIdentifiers.Quote(operation.StagingDatabaseName.Value)};"
                );
                await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                    $"UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = '{_foreignIdentity:D}'; CREATE TABLE public.collision_sentinel (marker text NOT NULL); INSERT INTO public.collision_sentinel (marker) VALUES ('foreign');"
                );
                _before = await ForeignEvidenceAsync(_lane.Target);
            };
            _result = await _lane.CreateAsync();
            _after = await ForeignEvidenceAsync(_lane.Target);
            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
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
        public void It_refuses_the_database_as_owned_by_another_identity() =>
            FailureOf(_result).Code.Should().Be(DmsProvisioningFailureCode.TargetUnowned);

        [Test]
        public void It_discovered_the_collision_by_the_rename_alone() =>
            _lane!
                .Engine.RenameOutcomes.Should()
                .ContainSingle()
                .Which.Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Failed>()
                .Which.Failure.Class.Should()
                .Be(DmsCommandFailureClass.DuplicateName);

        [Test]
        public void It_addressed_the_foreign_database_with_nothing_but_the_rename_and_the_reconcile_read() =>
            _lane!
                .TargetMutations.Select(call => call.Member)
                .Should()
                .Equal(
                    nameof(IDmsTargetEngine.RenameIntoTargetAsync),
                    nameof(IDmsTargetEngine.OpenSessionAsync)
                );

        [Test]
        public void It_left_the_foreign_database_exactly_as_it_was()
        {
            _before
                .Should()
                .Match<ForeignEvidence>(evidence =>
                    evidence.Identity == _foreignIdentity && evidence.Sentinel == "foreign"
                );
            _after.Should().Be(_before);
        }

        [Test]
        public void It_dropped_its_own_staging_database() =>
            _stagingAfter.Should().Be(new DmsLiveTargetState.Absent());
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_the_real_replay_runs
    {
        private Lane? _lane;
        private bool _replayObserved;
        private Exception? _thrown;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.PopulatedTemplateKind);
            _lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Sample, new LaneHooks());
            using CancellationTokenSource cancellation = new();
            Task<DmsDataStoreCreateResult> creating = _lane.Service.CreateAsync(
                _lane.Request,
                cancellation.Token
            );
            _replayObserved = await WaitUntilAsync(
                async () =>
                    _lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)) > 0
                    && await SessionCountNamedAsync(_lane.Operation.RestoreApplicationName) > 0,
                TimeSpan.FromMinutes(2)
            );
            await cancellation.CancelAsync();
            try
            {
                await creating;
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }

            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
            _targetAfter = await InspectAsync(_lane.Target);
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
        public void It_cancelled_the_replay_while_it_ran() =>
            (_replayObserved, _lane!.Engine.RestoreOutcomes.Single())
                .Should()
                .Be(
                    (
                        true,
                        new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                            new DmsCommandFailure(
                                DmsCommandFailureClass.Rejected,
                                DmsProvisioningDiagnostic.ForOperation(DmsEngineOperations.RestoreCancelled)
                            )
                        )
                    )
                );

        [Test]
        public void It_propagates_the_callers_cancellation() =>
            _thrown.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_drops_the_staging_database_and_leaves_the_target_absent() =>
            (_stagingAfter, _targetAfter, _lane!.OperationEntries.Length)
                .Should()
                .Be((new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent(), 0));
    }

    [TestFixture]
    public class Given_the_restore_bound_is_below_the_real_replay
    {
        private Lane? _lane;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.PopulatedTemplateKind);
            _lane = await Lane.BuildAsync(
                package,
                DmsDatabaseTemplate.Sample,
                new LaneHooks(),
                TimeSpan.FromSeconds(2)
            );
            _result = await _lane.CreateAsync();
            _stagingAfter = await InspectAsync(_lane.Operation.StagingDatabaseName);
            _targetAfter = await InspectAsync(_lane.Target);
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
        public void It_reports_a_transient_timeout()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Category, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TimedOut,
                        DmsProvisioningFailureCategory.Transient,
                        DmsEngineOperations.RestoreTimedOut
                    )
                );
        }

        [Test]
        public void It_drops_the_staging_database_and_leaves_the_target_absent() =>
            (
                ((DmsDataStoreCreateResult.Failure)_result).Cleanup.HasResidue,
                _lane!.Engine.CountOf(nameof(IDmsTargetEngine.DropStagingAsync)),
                _stagingAfter,
                _targetAfter
            )
                .Should()
                .Be((false, 1, new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent()));
    }

    [TestFixture]
    public class Given_the_allowlisted_package_is_tampered_after_its_pin_was_resolved
    {
        private Lane? _lane;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.MinimalTemplateKind);
            _lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, new LaneHooks());
            RealTemplatePackage.Tamper(_lane.PackagePath);
            _result = await _lane.CreateAsync();
            _targetAfter = await InspectAsync(_lane.Target);
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
        public void It_refuses_the_package_as_untrusted()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ArtifactUntrusted,
                        TemplateArtifactCatalog.VerifyAttestationStep
                            + "."
                            + nameof(TrustRefusalReason.PackageHashMismatch)
                    )
                );
        }

        [Test]
        public void It_makes_no_engine_call_and_leaves_the_target_absent() =>
            (_lane!.Engine.Calls.Count, _targetAfter, _lane.OperationEntries.Length)
                .Should()
                .Be((0, new DmsLiveTargetState.Absent(), 0));
    }

    [TestFixture]
    public class Given_the_allowlist_is_pointed_at_the_sql_server_package
    {
        private Lane? _lane;
        private Lane.Deployment _switched = null!;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.MinimalTemplateKind);
            RealTemplatePackage swapped = RealTemplatePackage.Find(
                RestoreManifest.MssqlEngine,
                RestoreManifest.MinimalTemplateKind
            );
            _lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, new LaneHooks());
            _switched = await _lane.SwitchAllowlistAsync(swapped);
            _result = await _switched.Service.CreateAsync(_lane.Request, default);
            _targetAfter = await InspectAsync(_lane.Target);
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
        public void It_refuses_the_manifests_engine()
        {
            DmsProvisioningFailure failure = FailureOf(_result);
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.ManifestMismatch,
                        TemplateArtifactCatalog.MatchConfigurationStep
                            + "."
                            + nameof(RestoreManifest.DatabaseEngine)
                    )
                );
        }

        [Test]
        public void It_makes_no_engine_call_and_leaves_the_target_absent() =>
            (_switched.Engine.Calls.Count, _targetAfter, _lane!.OperationEntries.Length)
                .Should()
                .Be((0, new DmsLiveTargetState.Absent(), 0));
    }
}
