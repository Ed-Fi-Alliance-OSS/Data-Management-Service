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
internal sealed class Lane : IAsyncDisposable
{
    private static readonly ProtectedDatabaseNames _protectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

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
