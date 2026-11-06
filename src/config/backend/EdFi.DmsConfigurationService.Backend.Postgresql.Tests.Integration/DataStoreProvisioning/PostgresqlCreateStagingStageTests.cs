// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The orchestrator's staging stage end to end (6.3 steps 2 to 7, Step 3.5) over the real catalog and the
/// real engine, with a package built from the committed DMS-shaped fixture: a valid package reaches the
/// publication boundary with a validated staging database and the selected target never addressed by
/// anything but an inspection (polled by a side session at every hook); a privilege drift and an
/// inventory drift in the artifact end with the staging database dropped and the target absent; a client
/// that loses the replay's result after the server committed continues; and a replay ended by the restore
/// bound is a transient timeout with the staging database dropped. Needs a real <c>psql</c>.
/// </summary>
[Category(Category)]
public class PostgresqlCreateStagingStageTests
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private static byte[] ShapedSql(string suffix = "") =>
        Encoding.UTF8.GetBytes(File.ReadAllText(FixturePath("dms-shaped-database.sql")) + suffix);

    private sealed class Ready : IDmsDataStoreProvisionerReadiness
    {
        public DmsProvisionerReadiness Current { get; } =
            new DmsProvisionerReadiness.Ready(
                DmsDataStoreProvisioningOptions.PostgresqlProvider,
                DateTimeOffset.UtcNow
            );
    }

    /// <summary>The D-23 seams with a probe the fixture runs at every hook point.</summary>
    private sealed class LaneHooks : IDmsProvisioningTestHooks
    {
        public List<DmsProvisioningHookPoint> Points { get; } = [];

        public HashSet<DmsProvisioningFaultPoint> Faults { get; } = [];

        public Func<DmsProvisioningHookPoint, DmsOperationId, Task>? Probe { get; set; }

        public async ValueTask AtAsync(
            DmsProvisioningHookPoint point,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Points.Add(point);
            if (Probe is not null)
            {
                await Probe(point, operation);
            }
        }

        public bool ShouldInjectFault(DmsProvisioningFaultPoint point, DmsOperationId operation) =>
            Faults.Contains(point);
    }

    /// <summary>The real engine behind a record of every call and the database name it addressed.</summary>
    private sealed class RecordingEngine(IDmsTargetEngine inner) : IDmsTargetEngine
    {
        public List<(string Member, string? DatabaseName)> Calls { get; } = [];

        public string Provider => inner.Provider;

        public Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken) =>
            inner.ValidateEnvironmentAsync(cancellationToken);

        public Task<DmsServerFacts> ReadServerFactsAsync(CancellationToken cancellationToken)
        {
            Calls.Add((nameof(ReadServerFactsAsync), null));
            return inner.ReadServerFactsAsync(cancellationToken);
        }

        public Task<DmsTargetLockResult> AcquireTargetLockAsync(
            DmsTargetDatabaseName name,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(AcquireTargetLockAsync), name.Value));
            return inner.AcquireTargetLockAsync(name, operation, cancellationToken);
        }

        public Task<DmsLiveTargetState> InspectAsync(
            DmsDatabaseName name,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(InspectAsync), name.Value));
            return inner.InspectAsync(name, cancellationToken);
        }

        public Task<DmsSessionOpenResult> OpenSessionAsync(
            DmsDatabaseName name,
            DmsInstanceIdentity expectedInstance,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(OpenSessionAsync), name.Value));
            return inner.OpenSessionAsync(name, expectedInstance, cancellationToken);
        }

        public Task<DmsIdentityRead> ReadIdentityAsync(
            DmsSession session,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(ReadIdentityAsync), null));
            return inner.ReadIdentityAsync(session, cancellationToken);
        }

        public Task<DmsTargetFacts> ReadTargetFactsAsync(
            DmsSession session,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(ReadTargetFactsAsync), null));
            return inner.ReadTargetFactsAsync(session, cancellationToken);
        }

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
            IDmsTargetLockHolder holder,
            DmsDatabaseName staging,
            DmsProvisionClaim claim,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(CreateStagingAsync), staging.Value));
            return inner.CreateStagingAsync(holder, staging, claim, cancellationToken);
        }

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
            DmsDatabaseName staging,
            StagedArtifact artifact,
            DmsOperationId operation,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(RestoreIntoStagingAsync), staging.Value));
            return inner.RestoreIntoStagingAsync(staging, artifact, operation, cancellationToken);
        }

        public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
            IDmsTargetLockHolder holder,
            DmsDatabaseName staging,
            DmsInstanceIdentity stagingInstance,
            DmsTargetDatabaseName target,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(RenameIntoTargetAsync), target.Value));
            return inner.RenameIntoTargetAsync(holder, staging, stagingInstance, target, cancellationToken);
        }

        public Task<DmsCommandOutcome> AssignMarkerAsync(
            DmsSession session,
            Guid expectedSourceIdentity,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(AssignMarkerAsync), null));
            return inner.AssignMarkerAsync(session, expectedSourceIdentity, cancellationToken);
        }

        public Task<DmsCommandOutcome> FenceAsync(
            IDmsTargetLockHolder holder,
            DmsTargetDatabaseName target,
            DmsInstanceIdentity instance,
            DmsServerSessionId? keep,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(FenceAsync), target.Value));
            return inner.FenceAsync(holder, target, instance, keep, cancellationToken);
        }

        public Task<DmsCommandOutcome> UnfenceAsync(
            IDmsTargetLockHolder holder,
            DmsTargetDatabaseName target,
            DmsInstanceIdentity instance,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(UnfenceAsync), target.Value));
            return inner.UnfenceAsync(holder, target, instance, cancellationToken);
        }

        public Task<DmsCommandOutcome> DropInstanceAsync(
            IDmsTargetLockHolder holder,
            DmsTargetDatabaseName target,
            DmsInstanceIdentity instance,
            DmsDropReason reason,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(DropInstanceAsync), target.Value));
            return inner.DropInstanceAsync(holder, target, instance, reason, cancellationToken);
        }

        public Task<DmsCommandOutcome> DropStagingAsync(
            DmsDatabaseName staging,
            DmsInstanceIdentity stagingInstance,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(DropStagingAsync), staging.Value));
            return inner.DropStagingAsync(staging, stagingInstance, cancellationToken);
        }

        public Task<DmsSessionTermination> TerminateSessionAsync(
            DmsServerSessionId session,
            TimeSpan deadline,
            CancellationToken cancellationToken
        )
        {
            Calls.Add((nameof(TerminateSessionAsync), null));
            return inner.TerminateSessionAsync(session, deadline, cancellationToken);
        }
    }

    /// <summary>
    /// One deployment: a catalog directory with a package built from the shaped fixture (its manifest
    /// carrying the committed canonical inventory and hash), an operations root, the real catalog, the real
    /// engine behind a recorder, and the orchestrator with the lane's hooks. Disposal drops whatever the
    /// operation left under its staging or target name and deletes the directories.
    /// </summary>
    private sealed class Lane : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _operations;

        private Lane(
            string root,
            string operations,
            RecordingEngine engine,
            DmsDataStoreProvisioningService service,
            DmsDataStoreCreateRequest request
        )
        {
            _root = root;
            _operations = operations;
            Engine = engine;
            Service = service;
            Request = request;
        }

        public RecordingEngine Engine { get; }

        public DmsDataStoreProvisioningService Service { get; }

        public DmsDataStoreCreateRequest Request { get; }

        public DmsTargetDatabaseName Target => Request.TargetName;

        public string[] OperationEntries => Directory.GetFileSystemEntries(_operations);

        private static DmsTargetDatabaseName TargetName(string purpose) =>
            DmsTargetDatabaseName.TryCreate(
                $"dms1438_{purpose}_{DmsOperationId.New().Value}",
                _protectedNames,
                out DmsTargetDatabaseName name
            ) == DmsTargetNameValidation.Valid
                ? name
                : throw new InvalidOperationException("The target name does not satisfy the rules.");

        public static async Task<Lane> BuildAsync(
            byte[] artifactSql,
            LaneHooks hooks,
            TimeSpan? restoreTimeout = null
        )
        {
            string root = Path.Combine(Path.GetTempPath(), "dms1438-lane-" + DmsOperationId.New().Value);
            string artifacts = Path.Combine(root, "artifacts");
            string operations = Path.Combine(root, "operations");
            Directory.CreateDirectory(artifacts);
            Directory.CreateDirectory(operations);

            TestTemplatePackage package = new TestTemplatePackageBuilder()
                .WithArtifact(artifactSql)
                .WithManifest(manifest =>
                {
                    manifest["inventory"] = JsonNode.Parse(
                        FixtureText("dms-shaped-database.inventory.canonical.json")
                    );
                    manifest["inventorySha256"] = FixtureText("dms-shaped-database.inventory.sha256");
                    manifest["projects"] = new JsonArray("edfi");
                    manifest["engineVersion"] = "18.0";
                    manifest["relationalMappingVersion"] = "v3";
                    manifest["effectiveSchemaHash"] = DmsShapedManifest.EffectiveSchemaHash;
                    manifest["resourceKeyCount"] = DmsShapedManifest.ResourceKeyCount;
                    manifest["resourceKeySeedHashB64"] = DmsShapedManifest.ResourceKeySeedHashB64;
                    manifest["apiSchemaFormatVersion"] = DmsShapedManifest.ApiSchemaFormatVersion;
                })
                .Build(artifacts);

            DmsDataStoreProvisioningOptions options = new()
            {
                Enabled = true,
                Provider = DmsDataStoreProvisioningOptions.PostgresqlProvider,
                AdminConnectionString = AdminConnectionString,
                TrustPolicyPath = package.TrustPolicyPath,
                ArtifactDirectory = artifacts,
                OperationDirectory = operations,
                Templates = new(StringComparer.Ordinal)
                {
                    [DmsDatabaseTemplate.MinimalWireValue] = new()
                    {
                        PackageId = package.PackageId,
                        PackageVersion = package.PackageVersion,
                    },
                },
                Compatibility = new()
                {
                    DataStandardVersion = "5.2.0",
                    EffectiveSchemaHash = DmsShapedManifest.EffectiveSchemaHash,
                    Projects = ["edfi"],
                },
                RestoreTimeout = restoreTimeout ?? TimeSpan.FromMinutes(5),
                CommandTimeout = TimeSpan.FromSeconds(30),
                SessionTerminationTimeout = TimeSpan.FromSeconds(30),
                CleanupAttempts = 3,
            };
            TemplateArtifactCatalog catalog = new(
                Options.Create(options),
                NullLogger<TemplateArtifactCatalog>.Instance
            );
            RecordingEngine engine = new(DmsProvisioningTestServer.Engine(restoreTimeout: restoreTimeout));
            DmsDataStoreProvisioningService service = new(
                new Ready(),
                engine,
                catalog,
                Options.Create(options),
                _protectedNames,
                TimeProvider.System,
                NullLogger<DmsDataStoreProvisioningService>.Instance
            )
            {
                TestHooks = hooks,
            };
            DmsTrustedArtifactPin pin = (await catalog.ResolveAsync(DmsDatabaseTemplate.Minimal, default))
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Subject.Pin;
            return new Lane(
                root,
                operations,
                engine,
                service,
                new DmsDataStoreCreateRequest(
                    DmsDatabaseTemplate.Minimal,
                    TargetName("stage"),
                    Guid.NewGuid(),
                    pin
                )
            );
        }

        public Task<DmsDataStoreCreateResult> CreateAsync() => Service.CreateAsync(Request, default);

        /// <summary>Every recorded call that named the selected target, other than the lock and inspections.</summary>
        public IReadOnlyList<(string Member, string? DatabaseName)> TargetMutations =>
            [
                .. Engine.Calls.Where(call =>
                    call.DatabaseName == Target.Value
                    && call.Member
                        is not (
                            nameof(IDmsTargetEngine.AcquireTargetLockAsync)
                            or nameof(IDmsTargetEngine.InspectAsync)
                        )
                ),
            ];

        public async ValueTask DisposeAsync()
        {
            await ExecuteOnServerAsync(
                $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(Target.Value)} WITH (FORCE);"
            );
            string claimSuffix = ":" + Request.ExpectedSourceIdentity.ToString("D");
            await using (NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default))
            {
                await using NpgsqlCommand command = new(
                    "SELECT d.datname FROM pg_catalog.pg_database d WHERE d.datname LIKE 'edfi_dms_provision_stage_%' AND pg_catalog.shobj_description(d.oid, 'pg_database') LIKE @suffix;",
                    connection
                );
                command.Parameters.AddWithValue("suffix", "%" + claimSuffix);
                List<string> leftovers = [];
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        leftovers.Add(reader.GetString(0));
                    }
                }

                foreach (string leftover in leftovers)
                {
                    await ExecuteOnServerAsync(
                        $"DROP DATABASE IF EXISTS {PostgresqlIdentifiers.Quote(leftover)} WITH (FORCE);"
                    );
                }
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Left for the operating system; nothing under test depends on it.
            }
        }
    }

    /// <summary>What a side session sees of the staging and target names at a hook point.</summary>
    private sealed record HookObservation(
        DmsProvisioningHookPoint Point,
        DmsLiveTargetState Staging,
        Guid? StagingIdentity,
        DmsLiveTargetState Target
    );

    private static async Task<HookObservation> ObserveAsync(
        DmsProvisioningHookPoint point,
        DmsOperationId operation,
        DmsTargetDatabaseName target
    )
    {
        PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
        DmsLiveTargetState staging = await side.InspectAsync(operation.StagingDatabaseName, default);
        Guid? identity = null;
        if (staging is DmsLiveTargetState.Present)
        {
            await using NpgsqlConnection connection = await AdminConnection()
                .OpenAsync(operation.StagingDatabaseName.Value, "dms1438-test", default);
            await using NpgsqlCommand command = new(
                "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";",
                connection
            );
            identity = await command.ExecuteScalarAsync() as Guid?;
        }

        return new HookObservation(point, staging, identity, await side.InspectAsync(target, default));
    }

    [TestFixture]
    public class Given_a_valid_package_reaches_the_publication_boundary
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private readonly List<HookObservation> _observations = [];
        private Exception? _outcome;
        private DmsLiveTargetState _stagingAfter = null!;
        private DmsLiveTargetState _targetAfter = null!;
        private string[] _entriesAfter = [];

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _lane = await Lane.BuildAsync(ShapedSql(), _hooks);
            _hooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _lane.Target));
            _outcome = await Record.ExceptionAsync(_lane.CreateAsync);
            DmsOperationId operation = DmsOperationId.TryParse(
                _lane
                    .Engine.Calls.First(call => call.Member == nameof(IDmsTargetEngine.CreateStagingAsync))
                    .DatabaseName![DmsOperationId.StagingDatabaseNamePrefix.Length..],
                out DmsOperationId parsed
            )
                ? parsed
                : throw new InvalidOperationException("The staging name does not carry the operation id.");
            PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
            _stagingAfter = await side.InspectAsync(operation.StagingDatabaseName, default);
            _targetAfter = await side.InspectAsync(_lane.Target, default);
            _entriesAfter = _lane.OperationEntries;
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
        public void It_stops_at_the_publication_stage() =>
            _outcome.Should().BeOfType<NotImplementedException>().Which.Message.Should().Contain("Step 3.6");

        [Test]
        public void It_had_a_validated_staging_database_and_no_target_at_the_rename_hook() =>
            _observations
                .Should()
                .ContainSingle(observation => observation.Point == DmsProvisioningHookPoint.BeforeRename)
                .Which.Should()
                .BeEquivalentTo(
                    new
                    {
                        StagingIdentity = DmsShapedManifest.SourceIdentity,
                        Target = new DmsLiveTargetState.Absent(),
                    }
                );

        [Test]
        public void It_never_addressed_the_target_with_a_mutation() =>
            _lane!.TargetMutations.Should().BeEmpty();

        [Test]
        public void It_dropped_the_staging_database_and_released_the_directory() =>
            (_stagingAfter, _targetAfter, _entriesAfter.Length)
                .Should()
                .Be((new DmsLiveTargetState.Absent(), new DmsLiveTargetState.Absent(), 0));
    }

    [TestFixture]
    public class Given_the_artifact_drifts_from_the_manifest
    {
        private Lane? _privilege;
        private Lane? _inventory;
        private readonly LaneHooks _privilegeHooks = new();
        private readonly LaneHooks _inventoryHooks = new();
        private readonly List<HookObservation> _observations = [];
        private DmsDataStoreCreateResult _privilegeResult = null!;
        private DmsDataStoreCreateResult _inventoryResult = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _privilege = await Lane.BuildAsync(
                ShapedSql("\nGRANT SELECT ON TABLE \"edfi\".\"School\" TO PUBLIC;\n"),
                _privilegeHooks
            );
            _privilegeHooks.Probe = async (point, operation) =>
                _observations.Add(await ObserveAsync(point, operation, _privilege.Target));
            _privilegeResult = await _privilege.CreateAsync();

            _inventory = await Lane.BuildAsync(ShapedSql("\nCREATE SCHEMA \"dmscs\";\n"), _inventoryHooks);
            _inventoryResult = await _inventory.CreateAsync();
            _targetAfter = await DmsProvisioningTestServer.Engine().InspectAsync(_privilege.Target, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_privilege is not null)
            {
                await _privilege.DisposeAsync();
            }

            if (_inventory is not null)
            {
                await _inventory.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_the_extra_grant_as_a_security_state_violation()
        {
            DmsProvisioningFailure failure = _privilegeResult
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.SecurityStateViolation,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.PrivilegesCheck
                    )
                );
        }

        [Test]
        public void It_reports_the_extra_schema_as_incompatible()
        {
            DmsProvisioningFailure failure = _inventoryResult
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
            (failure.Code, failure.Diagnostic.Operation)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCode.TargetIncompatible,
                        DmsDataStoreProvisioningService.ValidateStagingStep
                            + "."
                            + DmsTargetValidator.InventoryHashCheck
                    )
                );
        }

        [Test]
        public void It_never_reached_the_rename_hook_and_kept_the_target_absent()
        {
            _privilegeHooks.Points.Should().Equal(DmsProvisioningHookPoint.AfterRestoreStarted);
            _observations
                .Should()
                .OnlyContain(observation => observation.Target is DmsLiveTargetState.Absent);
            _targetAfter.Should().Be(new DmsLiveTargetState.Absent());
        }

        [Test]
        public void It_dropped_both_staging_databases_and_reported_no_residue() =>
            (
                ((DmsDataStoreCreateResult.Failure)_privilegeResult).Cleanup.HasResidue,
                ((DmsDataStoreCreateResult.Failure)_inventoryResult).Cleanup.HasResidue,
                _privilege!.Engine.Calls.Count(call =>
                    call.Member == nameof(IDmsTargetEngine.DropStagingAsync)
                ),
                _inventory!.Engine.Calls.Count(call =>
                    call.Member == nameof(IDmsTargetEngine.DropStagingAsync)
                )
            )
                .Should()
                .Be((false, false, 1, 1));
    }

    [TestFixture]
    public class Given_the_client_loses_the_restore_result_after_the_commit
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private Exception? _outcome;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _hooks.Faults.Add(DmsProvisioningFaultPoint.DropConnectionDuringRestore);
            _lane = await Lane.BuildAsync(ShapedSql(), _hooks);
            _outcome = await Record.ExceptionAsync(_lane.CreateAsync);
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
        public void It_reads_the_committed_state_and_continues_to_the_publication_boundary()
        {
            _outcome.Should().BeOfType<NotImplementedException>();
            _hooks.Points.Should().Contain(DmsProvisioningHookPoint.BeforeRename);
        }
    }

    [TestFixture]
    public class Given_the_replay_is_ended_by_the_restore_bound
    {
        private Lane? _lane;
        private readonly LaneHooks _hooks = new();
        private DmsDataStoreCreateResult _result = null!;
        private DmsLiveTargetState _targetAfter = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            RequirePsqlOrSkip();
            _lane = await Lane.BuildAsync(
                ShapedSql("\nSELECT pg_catalog.pg_sleep(60);\n"),
                _hooks,
                TimeSpan.FromSeconds(2)
            );
            _result = await _lane.CreateAsync();
            _targetAfter = await DmsProvisioningTestServer.Engine().InspectAsync(_lane.Target, default);
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
            DmsProvisioningFailure failure = _result
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject.Detail;
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
        public void It_dropped_the_staging_database_and_left_the_target_absent() =>
            (
                ((DmsDataStoreCreateResult.Failure)_result).Cleanup.HasResidue,
                _lane!.Engine.Calls.Count(call => call.Member == nameof(IDmsTargetEngine.DropStagingAsync)),
                _targetAfter
            )
                .Should()
                .Be((false, 1, new DmsLiveTargetState.Absent()));
    }
}
