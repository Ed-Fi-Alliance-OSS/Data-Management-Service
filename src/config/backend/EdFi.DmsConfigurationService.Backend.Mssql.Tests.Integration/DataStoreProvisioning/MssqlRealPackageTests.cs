// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Step 5.1 on SQL Server: the real feed packages in <c>DMS_TEMPLATE_PACKAGE_DIRECTORY</c>, signed with an ephemeral
/// key and allowlisted through the real catalog, staged through the directory shared with the server and restored.
/// Minimal and Sample each create a target that carries the expected identity, the manifest's effective schema and
/// canonical inventory, and passes every validation gate including security; a second create reconciles without
/// mutating; a delete drops it. A database restored out of band from the same backup (the same backup family, another
/// identity) takes the name before the rename and is refused untouched. A caller cancellation during the restore and a
/// restore bound below the real restore both drop the staging database and the staged file. A tampered package and a
/// package for the other engine are refused before any engine call.
/// </summary>
[Category(Category)]
public class MssqlRealPackageTests
{
    private static readonly Guid _foreignIdentity = Guid.Parse("5d4c3b2a-1f0e-4d9c-8b7a-6f5e4d3c2b1a");

    private static RealTemplatePackage RequireRealPackage(string templateKind)
    {
        RequireSharedStagingOrSkip();
        if (!RealTemplatePackage.IsConfigured)
        {
            Assert.Ignore(
                $"{RealTemplatePackage.DirectoryVariable} is not set; the real-package fixtures are skipped."
            );
        }

        return RealTemplatePackage.Find(RestoreManifest.MssqlEngine, templateKind);
    }

    private static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

    private static Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name) =>
        Engine().InspectAsync(name, default);

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
            MssqlDmsTargetEngine side = Engine();
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
                "SELECT CONCAT([EffectiveSchemaHash], N'|', [ResourceKeyCount], N'|', [ApiSchemaFormatVersion]) FROM [dms].[EffectiveSchema];"
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
        public void It_leaves_no_staging_database_staged_file_or_operation_entry() =>
            (
                _stagingAfter,
                ShapedBackup.StagedFilesOf(_lane!.Operation).Length,
                _lane.OperationEntries.Length
            )
                .Should()
                .Be((new DmsLiveTargetState.Absent(), 0, 0));
    }

    [TestFixture]
    public class Given_a_database_restored_from_the_same_backup_takes_the_target_name_before_the_rename
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private ForeignEvidence _before = null!;
        private ForeignEvidence _after = null!;
        private DmsLiveTargetState _stagingAfter = null!;

        private sealed record ForeignEvidence(
            DmsInstanceIdentity.Mssql Instance,
            Guid Identity,
            string Sentinel,
            int ObjectCount,
            string Inventory
        );

        private static async Task<ForeignEvidence> ForeignEvidenceAsync(DmsTargetDatabaseName target)
        {
            TestDatabase foreign = new(target.Value);
            DmsInstanceIdentity.Mssql instance = await foreign.CatalogInstanceAsync();
            ForeignEvidence evidence = new(
                instance,
                Guid.Parse(
                    await foreign.ScalarAsync<string>(
                        "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
                    )
                ),
                await foreign.ScalarAsync<string>(
                    "SELECT STRING_AGG([marker], N',') FROM [dbo].[collision_sentinel];"
                ),
                await foreign.ScalarAsync<int>(TargetEvidence.ObjectCountQuery),
                await TargetEvidence.InventoryFingerprintAsync(target, instance)
            );
            await WaitForNoSessionsOnAsync(target.Value);
            return evidence;
        }

        /// <summary>A server path in the instance's default directory for <paramref name="kind"/> (<c>Data</c> or <c>Log</c>).</summary>
        private static async Task<string> DefaultPathAsync(string kind, string fileName)
        {
            string directory = await ScalarOnServerAsync<string>(
                $"SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefault{kind}Path'));"
            );
            char separator = directory.Contains('\\') ? '\\' : '/';
            return directory.TrimEnd('/', '\\') + separator + fileName;
        }

        /// <summary>
        /// Restores the package's own backup under <paramref name="target"/> out of band, from a copy in the shared
        /// directory, with every file moved to a name of the target's own.
        /// </summary>
        private static async Task RestoreSameBackupAsync(RealTemplatePackage package, string target)
        {
            string fileName =
                $"dms1438-foreign-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.bak";
            string localPath = Path.Combine(SharedStagingPath!, fileName);
            package.ExtractArtifactTo(localPath);
            try
            {
                string device = MssqlIdentifiers.Literal(
                    SharedStagingServerPath!.TrimEnd('/') + "/" + fileName
                );
                List<(string LogicalName, bool IsLog)> files = [];
                await using (SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default))
                {
                    await using SqlCommand command = new(
                        $"RESTORE FILELISTONLY FROM DISK = {device};",
                        connection
                    );
                    await using SqlDataReader reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        files.Add(
                            (
                                reader.GetString(reader.GetOrdinal("LogicalName")),
                                reader.GetString(reader.GetOrdinal("Type")) == "L"
                            )
                        );
                    }
                }

                List<string> moves = [];
                for (int index = 0; index < files.Count; index++)
                {
                    string path = await DefaultPathAsync(
                        files[index].IsLog ? "Log" : "Data",
                        $"{target}_{index}{(files[index].IsLog ? ".ldf" : ".mdf")}"
                    );
                    moves.Add(
                        $"MOVE {MssqlIdentifiers.Literal(files[index].LogicalName)} TO {MssqlIdentifiers.Literal(path)}"
                    );
                }

                await ExecuteOnServerAsync(
                    $"RESTORE DATABASE {MssqlIdentifiers.Quote(target)} FROM DISK = {device} WITH {string.Join(", ", moves)};"
                );
            }
            finally
            {
                File.Delete(localPath);
            }
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.MinimalTemplateKind);
            _lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Minimal, _hooks);
            _hooks.Probe = async (point, _) =>
            {
                if (point != DmsProvisioningHookPoint.BeforeRename)
                {
                    return;
                }

                await RestoreSameBackupAsync(package, _lane.Target.Value);
                await new TestDatabase(_lane.Target.Value).ExecuteAsync(
                    $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = '{_foreignIdentity:D}'; CREATE TABLE [dbo].[collision_sentinel] ([marker] nvarchar(50) NOT NULL); INSERT INTO [dbo].[collision_sentinel] ([marker]) VALUES (N'foreign');"
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
    public class Given_the_caller_cancels_while_the_real_restore_runs
    {
        private Lane? _lane;
        private bool _restoreObserved;
        private Exception? _thrown;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;

        /// <summary>Requests a session named <paramref name="applicationName"/> is running as a <c>RESTORE</c>.</summary>
        private static Task<int> RestoreRequestsOfAsync(string applicationName) =>
            ScalarOnServerAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.program_name = @app AND r.command LIKE N'RESTORE%';",
                ("@app", applicationName)
            );

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.PopulatedTemplateKind);
            _lane = await Lane.BuildAsync(package, DmsDatabaseTemplate.Sample, new LaneHooks());
            using CancellationTokenSource cancellation = new();
            Task<DmsDataStoreCreateResult> creating = _lane.CreateAsync(cancellation.Token);
            _restoreObserved = await WaitUntilAsync(
                async () =>
                    _lane.Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)) > 0
                    && await RestoreRequestsOfAsync(_lane.Operation.RestoreApplicationName) > 0,
                TimeSpan.FromMinutes(3)
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
        public void It_cancelled_while_the_server_ran_the_restore() => _restoreObserved.Should().BeTrue();

        [Test]
        public void It_propagates_the_callers_cancellation() =>
            _thrown.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_drops_the_staging_database_and_the_staged_file_and_leaves_the_target_absent() =>
            (
                _stagingAfter,
                _targetAfter,
                ShapedBackup.StagedFilesOf(_lane!.Operation).Length,
                _lane.OperationEntries.Length
            )
                .Should()
                .Be((new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent(), 0, 0));
    }

    [TestFixture]
    public class Given_the_restore_bound_is_below_the_real_restore
    {
        private Lane? _lane;
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RealTemplatePackage package = RequireRealPackage(RestoreManifest.PopulatedTemplateKind);

            // The Sample restore alone takes seconds (about 2.5 s on a developer machine); a quarter second ends any
            // real one while the server is restoring.
            _lane = await Lane.BuildAsync(
                package,
                DmsDatabaseTemplate.Sample,
                new LaneHooks(),
                TimeSpan.FromMilliseconds(250)
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
        public void It_drops_the_staging_database_and_the_staged_file_and_leaves_the_target_absent() =>
            (
                ((DmsDataStoreCreateResult.Failure)_result).Cleanup.HasResidue,
                _stagingAfter,
                _targetAfter,
                ShapedBackup.StagedFilesOf(_lane!.Operation).Length
            )
                .Should()
                .Be((false, new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent(), 0));
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
    public class Given_the_allowlist_is_pointed_at_the_postgresql_package
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
                RestoreManifest.PostgresqlEngine,
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
