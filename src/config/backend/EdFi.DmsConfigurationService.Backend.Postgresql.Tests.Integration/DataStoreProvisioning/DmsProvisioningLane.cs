// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>Read-only evidence about a database under a target name, taken on side connections, that a test compares before and after.</summary>
internal static class TargetEvidence
{
    /// <summary>Relations and routines outside the system schemas.</summary>
    public const string ObjectCountQuery =
        "SELECT (SELECT COUNT(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\\_toast%') + (SELECT COUNT(*) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema'));";

    /// <summary>The canonical inventory hash the engine's facts describe, or the reason it cannot be built.</summary>
    public static async Task<string> InventoryFingerprintAsync(
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance
    )
    {
        PostgresqlDmsTargetEngine side = DmsProvisioningTestServer.Engine();
        DmsSession session = (await side.OpenSessionAsync(target, instance, default))
            .Should()
            .BeOfType<DmsSessionOpenResult.Opened>()
            .Subject.Session;
        await using (session)
        {
            DmsTargetFacts facts;
            try
            {
                facts = await side.ReadTargetFactsAsync(session, default);
            }
            catch (DmsProvisioningFailedException exception)
            {
                return "unreadable:" + exception.Failure.Diagnostic.Operation;
            }

            return DmsTargetValidator.TryBuildInventory(
                facts,
                out CanonicalInventory? inventory,
                out string? defect
            )
                ? CanonicalInventoryHash.Compute(inventory)
                : "defect:" + defect;
        }
    }
}

/// <summary>The D-23 seams with a probe the fixture runs at every hook point.</summary>
internal sealed class LaneHooks : IDmsProvisioningTestHooks
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

/// <summary>
/// The real engine behind a record of every call and the database name it addressed, every rename outcome,
/// and two client-side faults a fixture can install: <see cref="AfterRename"/> replaces the outcome of a
/// rename the server already ran (a lost result), and <see cref="InsteadOfMarker"/> answers for a marker
/// that is never sent (an outcome in doubt that did not commit).
/// </summary>
internal sealed class RecordingEngine(IDmsTargetEngine inner) : IDmsTargetEngine
{
    public List<(string Member, string? DatabaseName)> Calls { get; } = [];

    public List<DmsCommandOutcome<DmsInstanceIdentity>> RenameOutcomes { get; } = [];

    /// <summary>The restore's outcomes as the engine returned them.</summary>
    public List<DmsCommandOutcome<DmsInstanceIdentity>> RestoreOutcomes { get; } = [];

    public delegate DmsCommandOutcome<DmsInstanceIdentity> RenameFault(
        DmsCommandOutcome<DmsInstanceIdentity> outcome,
        IDmsTargetLockHolder holder
    );

    public RenameFault? AfterRename { get; set; }

    public Func<DmsSession, DmsCommandOutcome>? InsteadOfMarker { get; set; }

    public string Provider => inner.Provider;

    public int CountOf(string member) => Calls.Count(call => call.Member == member);

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

    public Task<DmsLiveTargetState> InspectAsync(DmsDatabaseName name, CancellationToken cancellationToken)
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

    public Task<DmsIdentityRead> ReadIdentityAsync(DmsSession session, CancellationToken cancellationToken)
    {
        Calls.Add((nameof(ReadIdentityAsync), null));
        return inner.ReadIdentityAsync(session, cancellationToken);
    }

    public Task<DmsTargetFacts> ReadTargetFactsAsync(DmsSession session, CancellationToken cancellationToken)
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

    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        DmsTargetDatabaseName target,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    )
    {
        Calls.Add((nameof(RestoreIntoStagingAsync), staging.Value));
        DmsCommandOutcome<DmsInstanceIdentity> outcome = await inner.RestoreIntoStagingAsync(
            staging,
            target,
            artifact,
            operation,
            cancellationToken
        );
        RestoreOutcomes.Add(outcome);
        return outcome;
    }

    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        Calls.Add((nameof(RenameIntoTargetAsync), target.Value));
        DmsCommandOutcome<DmsInstanceIdentity> outcome = await inner.RenameIntoTargetAsync(
            holder,
            staging,
            stagingInstance,
            target,
            cancellationToken
        );
        RenameOutcomes.Add(outcome);
        return AfterRename is null ? outcome : AfterRename(outcome, holder);
    }

    public Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    )
    {
        Calls.Add((nameof(AssignMarkerAsync), null));
        return InsteadOfMarker is null
            ? inner.AssignMarkerAsync(session, expectedSourceIdentity, cancellationToken)
            : Task.FromResult(InsteadOfMarker(session));
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

    public Task<DmsIdentityRead> ReadIdentityOnHolderAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        Calls.Add((nameof(ReadIdentityOnHolderAsync), target.Value));
        return inner.ReadIdentityOnHolderAsync(holder, target, cancellationToken);
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
/// carrying the committed canonical inventory and hash) or a real feed package signed with an ephemeral key
/// (Step 5.1), an operations root, the real catalog, the real
/// engine behind a recorder, and the orchestrator with the lane's hooks. Disposal drops whatever the
/// operation left under its staging or target name and deletes the directories.
/// </summary>
internal sealed class Lane : IAsyncDisposable
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    private readonly string _root;
    private readonly string _operations;
    private readonly LaneHooks _hooks;
    private readonly TimeSpan? _restoreTimeout;
    private readonly TimeSpan? _lockHeartbeatInterval;
    private readonly Deployment _deployment;

    private Lane(
        string root,
        string operations,
        LaneHooks hooks,
        TimeSpan? restoreTimeout,
        TimeSpan? lockHeartbeatInterval,
        Deployment deployment,
        DmsDataStoreCreateRequest request
    )
    {
        _root = root;
        _operations = operations;
        _hooks = hooks;
        _restoreTimeout = restoreTimeout;
        _lockHeartbeatInterval = lockHeartbeatInterval;
        _deployment = deployment;
        Engine = deployment.Engine;
        Service = deployment.Service;
        Request = request;
    }

    private sealed class Ready : IDmsDataStoreProvisionerReadiness
    {
        public DmsProvisionerReadiness Current { get; } =
            new DmsProvisionerReadiness.Ready(
                DmsDataStoreProvisioningOptions.PostgresqlProvider,
                DateTimeOffset.UtcNow
            );
    }

    public RecordingEngine Engine { get; }

    public DmsDataStoreProvisioningService Service { get; }

    public DmsDataStoreCreateRequest Request { get; }

    public DmsTargetDatabaseName Target => Request.TargetName;

    /// <summary>The allowlisted package in the lane's catalog directory.</summary>
    public string PackagePath => _deployment.PackagePath;

    /// <summary>A delete of the lane's target under the record's expected identity.</summary>
    public DmsDataStoreDeleteRequest DeleteRequest => new(Target, Request.ExpectedSourceIdentity);

    public string[] OperationEntries => Directory.GetFileSystemEntries(_operations);

    /// <summary>The operation id, read from the staging name the create addressed.</summary>
    public DmsOperationId Operation =>
        DmsOperationId.TryParse(
            Engine
                .Calls.First(call => call.Member == nameof(IDmsTargetEngine.CreateStagingAsync))
                .DatabaseName![DmsOperationId.StagingDatabaseNamePrefix.Length..],
            out DmsOperationId parsed
        )
            ? parsed
            : throw new InvalidOperationException("The staging name does not carry the operation id.");

    /// <summary>The committed DMS-shaped fixture as an artifact, with <paramref name="suffix"/> appended.</summary>
    public static byte[] ShapedSql(string suffix = "") =>
        Encoding.UTF8.GetBytes(File.ReadAllText(FixturePath("dms-shaped-database.sql")) + suffix);

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
        TimeSpan? restoreTimeout = null,
        TimeSpan? lockHeartbeatInterval = null
    )
    {
        (string root, string operations) = NewRoot();
        Deployment deployment = await DeployAsync(
            root,
            operations,
            artifactSql,
            null,
            hooks,
            restoreTimeout,
            lockHeartbeatInterval
        );
        return new Lane(
            root,
            operations,
            hooks,
            restoreTimeout,
            lockHeartbeatInterval,
            deployment,
            new DmsDataStoreCreateRequest(
                DmsDatabaseTemplate.Minimal,
                TargetName("stage"),
                Guid.NewGuid(),
                deployment.Pin
            )
        );
    }

    /// <summary>
    /// A deployment of a real feed package (Step 5.1) under <paramref name="template"/>: the package signed with an
    /// ephemeral key into the lane's catalog directory, and <c>Compatibility</c> taken from its manifest.
    /// </summary>
    public static async Task<Lane> BuildAsync(
        RealTemplatePackage package,
        DmsDatabaseTemplate template,
        LaneHooks hooks,
        TimeSpan? restoreTimeout = null
    )
    {
        (string root, string operations) = NewRoot();
        Deployment deployment = await DeployRealAsync(
            root,
            operations,
            package,
            template,
            package.Compatibility(),
            null,
            hooks,
            restoreTimeout
        );
        return new Lane(
            root,
            operations,
            hooks,
            restoreTimeout,
            null,
            deployment,
            new DmsDataStoreCreateRequest(template, TargetName("real"), Guid.NewGuid(), deployment.Pin)
        );
    }

    private static (string Root, string Operations) NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dms1438-lane-" + DmsOperationId.New().Value);
        string operations = Path.Combine(root, "operations");
        Directory.CreateDirectory(operations);
        return (root, operations);
    }

    /// <summary>One configured service over the shared server: its own recorder, catalog and allowlisted package.</summary>
    internal sealed record Deployment(
        RecordingEngine Engine,
        DmsDataStoreProvisioningService Service,
        DmsTrustedArtifactPin Pin,
        DmsDataStoreProvisioningOptions Options,
        TemplateArtifactCatalog Catalog,
        string PackagePath
    );

    /// <summary>
    /// The lane's own deployment (same options, catalog, allowlisted package and hooks, so the lane's request
    /// and pin apply) over <paramref name="inner"/> instead of the superuser engine, behind a fresh recorder.
    /// </summary>
    public Deployment WithEngine(IDmsTargetEngine inner)
    {
        RecordingEngine engine = new(inner);
        return _deployment with
        {
            Engine = engine,
            Service = ServiceFor(engine, _deployment.Catalog, _deployment.Options, _hooks),
        };
    }

    /// <summary>
    /// The deployment after its allowlist was switched to another package built from <paramref name="artifactSql"/>
    /// under <paramref name="packageVersion"/>, with the same manifest content, in its own catalog directory; it
    /// shares the lane's server, operations root and hooks, so it addresses the same target.
    /// </summary>
    public Task<Deployment> SwitchAllowlistAsync(byte[] artifactSql, string packageVersion) =>
        DeployAsync(
            _root,
            _operations,
            artifactSql,
            packageVersion,
            _hooks,
            _restoreTimeout,
            _lockHeartbeatInterval
        );

    /// <summary>
    /// The deployment after an operator pointed the lane template's allowlist entry at another real
    /// <paramref name="package"/>, signed into its own catalog directory, while the lane's <c>Compatibility</c> and
    /// record (with its pin) stay as they were; the package is not resolved, since a test may expect it refused.
    /// </summary>
    public Task<Deployment> SwitchAllowlistAsync(RealTemplatePackage package) =>
        DeployRealAsync(
            _root,
            _operations,
            package,
            Request.Template,
            _deployment.Options.Compatibility!,
            Request.PinnedArtifact,
            _hooks,
            _restoreTimeout
        );

    private static async Task<Deployment> DeployRealAsync(
        string root,
        string operations,
        RealTemplatePackage package,
        DmsDatabaseTemplate template,
        DmsDataStoreProvisioningOptions.CompatibilityExpectations compatibility,
        DmsTrustedArtifactPin? pin,
        LaneHooks hooks,
        TimeSpan? restoreTimeout
    )
    {
        string artifacts = Path.Combine(root, "artifacts-" + DmsOperationId.New().Value);
        RealTemplatePublication publication;
        using (EphemeralAttestationSigner signer = new(RealTemplatePackage.ProducerName))
        {
            publication = package.PublishTo(artifacts, signer);
        }

        return await DeployOverAsync(
            new Allowlisted(
                template,
                package.PackageId,
                package.PackageVersion,
                publication.PackagePath,
                publication.TrustPolicyPath,
                artifacts
            ),
            compatibility,
            operations,
            hooks,
            restoreTimeout,
            null,
            pin
        );
    }

    /// <summary>A package signed into a catalog directory and the allowlist entry that names it.</summary>
    private sealed record Allowlisted(
        DmsDatabaseTemplate Template,
        string PackageId,
        string PackageVersion,
        string PackagePath,
        string TrustPolicyPath,
        string ArtifactDirectory
    );

    private static async Task<Deployment> DeployAsync(
        string root,
        string operations,
        byte[] artifactSql,
        string? packageVersion,
        LaneHooks hooks,
        TimeSpan? restoreTimeout,
        TimeSpan? lockHeartbeatInterval
    )
    {
        string artifacts = Path.Combine(root, "artifacts-" + DmsOperationId.New().Value);
        Directory.CreateDirectory(artifacts);

        TestTemplatePackageBuilder builder = new();
        if (packageVersion is not null)
        {
            builder.WithPackageVersion(packageVersion);
        }

        TestTemplatePackage package = builder
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

        return await DeployOverAsync(
            new Allowlisted(
                DmsDatabaseTemplate.Minimal,
                package.PackageId,
                package.PackageVersion,
                package.PackagePath,
                package.TrustPolicyPath,
                artifacts
            ),
            new()
            {
                DataStandardVersion = "5.2.0",
                EffectiveSchemaHash = DmsShapedManifest.EffectiveSchemaHash,
                Projects = ["edfi"],
            },
            operations,
            hooks,
            restoreTimeout,
            lockHeartbeatInterval,
            null
        );
    }

    /// <summary>
    /// The options, real catalog, recorded engine and orchestrator over <paramref name="allowlisted"/>; the pin is
    /// <paramref name="pin"/>, or the one the catalog resolves now, which must succeed.
    /// </summary>
    private static async Task<Deployment> DeployOverAsync(
        Allowlisted allowlisted,
        DmsDataStoreProvisioningOptions.CompatibilityExpectations compatibility,
        string operations,
        LaneHooks hooks,
        TimeSpan? restoreTimeout,
        TimeSpan? lockHeartbeatInterval,
        DmsTrustedArtifactPin? pin
    )
    {
        DmsDataStoreProvisioningOptions options = new()
        {
            Enabled = true,
            Provider = DmsDataStoreProvisioningOptions.PostgresqlProvider,
            AdminConnectionString = AdminConnectionString,
            TrustPolicyPath = allowlisted.TrustPolicyPath,
            ArtifactDirectory = allowlisted.ArtifactDirectory,
            OperationDirectory = operations,
            Templates = new(StringComparer.Ordinal)
            {
                [allowlisted.Template.WireValue] = new()
                {
                    PackageId = allowlisted.PackageId,
                    PackageVersion = allowlisted.PackageVersion,
                },
            },
            Compatibility = compatibility,
            RestoreTimeout = restoreTimeout ?? TimeSpan.FromMinutes(5),
            CommandTimeout = TimeSpan.FromSeconds(30),
            SessionTerminationTimeout = TimeSpan.FromSeconds(30),
            CleanupAttempts = 3,
        };
        TemplateArtifactCatalog catalog = new(
            Options.Create(options),
            NullLogger<TemplateArtifactCatalog>.Instance
        );
        RecordingEngine engine = new(
            DmsProvisioningTestServer.Engine(
                lockHeartbeatInterval: lockHeartbeatInterval,
                restoreTimeout: restoreTimeout
            )
        );
        DmsDataStoreProvisioningService service = ServiceFor(engine, catalog, options, hooks);
        DmsTrustedArtifactPin resolved =
            pin
            ?? (await catalog.ResolveAsync(allowlisted.Template, default))
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Subject.Pin;
        return new Deployment(engine, service, resolved, options, catalog, allowlisted.PackagePath);
    }

    private static DmsDataStoreProvisioningService ServiceFor(
        RecordingEngine engine,
        TemplateArtifactCatalog catalog,
        DmsDataStoreProvisioningOptions options,
        LaneHooks hooks
    ) =>
        new(
            new Ready(),
            engine,
            catalog,
            Options.Create(options),
            _protectedNames,
            DmsProvisioningTestServer.AdminConnectionSource(),
            TimeProvider.System,
            NullLogger<DmsDataStoreProvisioningService>.Instance
        )
        {
            TestHooks = hooks,
        };

    public Task<DmsDataStoreCreateResult> CreateAsync() => Service.CreateAsync(Request, default);

    public Task<DmsDataStoreDeleteResult> DeleteAsync() => Service.DeleteAsync(DeleteRequest, default);

    /// <summary>
    /// Builds the lane's target without <c>psql</c>: the committed DMS-shaped fixture under the target name,
    /// carrying <paramref name="sourceIdentity"/> (the record's expected identity by default). A delete needs
    /// only the identity, so this stands in for a create on lanes without a replay client.
    /// </summary>
    public async Task<DmsInstanceIdentity.Postgresql> SeedTargetAsync(Guid? sourceIdentity = null)
    {
        await EnsureEnqueueOwnerRoleAsync();
        await ExecuteOnServerAsync($"CREATE DATABASE {PostgresqlIdentifiers.Quote(Target.Value)};");
        TestDatabase target = new(Target.Value);
        await target.ExecuteAsync(await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql")));
        await target.ExecuteAsync(
            $"UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = '{(sourceIdentity ?? Request.ExpectedSourceIdentity):D}';"
        );
        return await target.CatalogInstanceAsync();
    }

    /// <summary>Whether the target exists, with its <c>oid</c> and whether it accepts connections, read on a side connection.</summary>
    public async Task<(bool Exists, uint Oid, bool AllowsConnections)> TargetCatalogAsync()
    {
        await using NpgsqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
        await using NpgsqlCommand command = new(
            "SELECT d.oid, d.datallowconn FROM pg_catalog.pg_database d WHERE d.datname = @name;",
            connection
        );
        command.Parameters.AddWithValue("name", Target.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (true, await reader.GetFieldValueAsync<uint>(0), reader.GetBoolean(1))
            : (false, 0, false);
    }

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

    /// <summary>The <c>SourceIdentity</c> under the target name, read on a side connection.</summary>
    public async Task<Guid> TargetIdentityAsync()
    {
        await using NpgsqlConnection connection = await AdminConnection()
            .OpenAsync(Target.Value, "dms1438-test", default);
        await using NpgsqlCommand command = new(
            "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";",
            connection
        );
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

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
