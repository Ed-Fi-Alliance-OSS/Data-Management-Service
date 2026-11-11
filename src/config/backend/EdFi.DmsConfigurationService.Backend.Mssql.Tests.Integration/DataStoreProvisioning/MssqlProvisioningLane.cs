// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// Backups of the committed DMS-shaped fixture made by the server itself, so the bytes are a real full backup. The
/// server writes them into its own default backup directory under fixed names with <c>INIT</c> (each run overwrites the
/// last one's file) and the bytes are read back over SQL: SQL Server on Linux cannot write a backup into a Docker
/// Desktop bind mount (OS error 31 on <c>DiskChangeFileSize</c>), while the staged file it later restores from the shared
/// directory is only read.
/// </summary>
internal static class ShapedBackup
{
    /// <summary>
    /// The fixture plus <paramref name="suffixSql"/> in a fresh database, backed up by the server
    /// (<paramref name="appendSecondSet"/> appends a second backup set to the same device); the database is dropped
    /// before the bytes are returned.
    /// </summary>
    public static async Task<byte[]> CreateAsync(string suffixSql = "", bool appendSecondSet = false)
    {
        await using TestDatabase source = await CreateDmsShapedDatabaseAsync("reference");
        if (suffixSql.Length > 0)
        {
            await source.ExecuteAsync(suffixSql);
        }

        string serverPath = await BackupPathAsync(
            appendSecondSet ? "dms1438-reference-two-sets.bak" : "dms1438-reference.bak"
        );
        string quoted = MssqlIdentifiers.Quote(source.Name);
        string device = MssqlIdentifiers.Literal(serverPath);
        await ExecuteOnServerAsync(
            appendSecondSet
                ? $"BACKUP DATABASE {quoted} TO DISK = {device} WITH INIT, FORMAT, COPY_ONLY;\nGO\nBACKUP DATABASE {quoted} TO DISK = {device} WITH NOINIT, COPY_ONLY;"
                : $"BACKUP DATABASE {quoted} TO DISK = {device} WITH INIT, FORMAT, COPY_ONLY;"
        );
        return await ScalarOnServerAsync<byte[]>(
            $"SELECT b.BulkColumn FROM OPENROWSET(BULK {device}, SINGLE_BLOB) AS b;"
        );
    }

    /// <summary>Writes <paramref name="bytes"/> where an extracted artifact would be and describes it.</summary>
    public static StagedArtifact Extracted(string directory, byte[] bytes)
    {
        string path = Path.Combine(directory, "artifact.bak");
        File.WriteAllBytes(path, bytes);
        return new StagedArtifact(
            path,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            DmsArtifactKind.MssqlBackup
        );
    }

    /// <summary>The staged files of <paramref name="operation"/> left in the shared staging directory.</summary>
    public static string[] StagedFilesOf(DmsOperationId operation) =>
        Directory.GetFiles(SharedStagingPath!, MssqlBackupStager.FileNameFor(operation));
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
/// The real SQL Server engine behind a record of every call and the database name it addressed, of every instance a
/// staging drop, a session open or a compensation drop was handed, and of every restore and rename outcome. Two seams
/// stand in for a lost result: <see cref="AfterRename"/> replaces the rename's outcome after the server ran it, and
/// <see cref="InsteadOfMarker"/> answers for a marker that is never sent.
/// </summary>
internal sealed class RecordingEngine(MssqlDmsTargetEngine inner) : IDmsTargetEngine
{
    public List<(string Member, string? DatabaseName)> Calls { get; } = [];

    public List<DmsInstanceIdentity> DroppedStagingInstances { get; } = [];

    /// <summary>The instance each session open was asked to verify, in order.</summary>
    public List<DmsInstanceIdentity> OpenedInstances { get; } = [];

    /// <summary>The instance each target drop was handed, in order.</summary>
    public List<DmsInstanceIdentity> DroppedInstances { get; } = [];

    public List<DmsCommandOutcome<DmsInstanceIdentity>> RestoreOutcomes { get; } = [];

    /// <summary>The rename's outcomes as the engine returned them, before <see cref="AfterRename"/>.</summary>
    public List<DmsCommandOutcome<DmsInstanceIdentity>> RenameOutcomes { get; } = [];

    /// <summary>Replaces a rename's outcome after the server ran it, on the holder that ran it.</summary>
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
        OpenedInstances.Add(expectedInstance);
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
        CancellationToken cancellationToken,
        Action? restoreStarting = null
    )
    {
        Calls.Add((nameof(RestoreIntoStagingAsync), staging.Value));
        DmsCommandOutcome<DmsInstanceIdentity> outcome = await inner.RestoreIntoStagingAsync(
            staging,
            target,
            artifact,
            operation,
            cancellationToken,
            restoreStarting
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
        DroppedInstances.Add(instance);
        return inner.DropInstanceAsync(holder, target, instance, reason, cancellationToken);
    }

    public Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    )
    {
        Calls.Add((nameof(DropStagingAsync), staging.Value));
        DroppedStagingInstances.Add(stagingInstance);
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
/// What a test compares about a database it must leave untouched: user objects and types, and the canonical inventory
/// hash the engine's facts describe.
/// </summary>
internal static class TargetEvidence
{
    /// <summary>User objects, user types and user schemas: what a DDL statement against the database would change.</summary>
    public const string ObjectCountQuery =
        "SELECT (SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0) + (SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1) + (SELECT COUNT(*) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383);";

    /// <summary>The canonical inventory hash the engine's facts describe, or the reason it cannot be built.</summary>
    public static async Task<string> InventoryFingerprintAsync(
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance
    )
    {
        MssqlDmsTargetEngine side = Engine();
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

/// <summary>
/// One SQL Server deployment over the shared server and the shared staging directory: a catalog directory with a
/// package whose artifact is a server-made backup of the DMS-shaped fixture (its manifest carrying the committed
/// canonical inventory and hash and the live compatibility level), or a real feed package signed with an ephemeral key
/// (Step 5.1), an operations root, the real catalog, the real engine behind a recorder, and the orchestrator with the
/// lane's hooks. <see cref="WithEngine"/> and both <c>SwitchAllowlistAsync</c> overloads give further deployments over
/// the same target. Disposal drops whatever the operation left under its staging or target name and deletes the
/// directories.
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
    private readonly Deployment _deployment;

    private Lane(
        string root,
        string operations,
        LaneHooks hooks,
        Deployment deployment,
        DmsDataStoreCreateRequest request
    )
    {
        _root = root;
        _operations = operations;
        _hooks = hooks;
        _deployment = deployment;
        Engine = deployment.Engine;
        Service = deployment.Service;
        Request = request;
    }

    private sealed class Ready : IDmsDataStoreProvisionerReadiness
    {
        public DmsProvisionerReadiness Current { get; } =
            new DmsProvisionerReadiness.Ready(
                DmsDataStoreProvisioningOptions.MssqlProvider,
                DateTimeOffset.UtcNow
            );
    }

    public RecordingEngine Engine { get; }

    public DmsDataStoreProvisioningService Service { get; }

    public DmsDataStoreCreateRequest Request { get; }

    public DmsTargetDatabaseName Target => Request.TargetName;

    /// <summary>The allowlisted package in the lane's catalog directory.</summary>
    public string PackagePath => _deployment.PackagePath;

    /// <summary>What the deployments left in the operations root; a finished operation leaves nothing.</summary>
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

    public Task<DmsDataStoreCreateResult> CreateAsync(CancellationToken cancellationToken = default) =>
        Service.CreateAsync(Request, cancellationToken);

    /// <summary>A delete of the lane's target under the record's expected identity.</summary>
    public DmsDataStoreDeleteRequest DeleteRequest => new(Target, Request.ExpectedSourceIdentity);

    public Task<DmsDataStoreDeleteResult> DeleteAsync() => Service.DeleteAsync(DeleteRequest, default);

    /// <summary>Artifact bytes for a lane that only deletes: the catalog needs a package, a delete never restores it.</summary>
    public static byte[] DeleteOnlyArtifact => "TAPE\0delete-only lane\n"u8.ToArray();

    /// <summary>
    /// Builds the lane's target without a restore: the committed DMS-shaped fixture under the target name, carrying
    /// <paramref name="sourceIdentity"/> (the record's expected identity by default). A delete needs only the identity.
    /// </summary>
    public async Task<DmsInstanceIdentity.Mssql> SeedTargetAsync(Guid? sourceIdentity = null)
    {
        await ExecuteOnServerAsync($"CREATE DATABASE {MssqlIdentifiers.Quote(Target.Value)};");
        TestDatabase target = new(Target.Value);
        await target.ExecuteAsync(await File.ReadAllTextAsync(FixturePath("dms-shaped-database.sql")));
        await target.ExecuteAsync(
            $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = '{(sourceIdentity ?? Request.ExpectedSourceIdentity):D}';"
        );
        DmsInstanceIdentity.Mssql instance = await target.CatalogInstanceAsync();
        await WaitForNoSessionsOnAsync(Target.Value);
        return instance;
    }

    /// <summary>Whether the target exists, with its <c>database_id</c> and <c>user_access_desc</c>, read on a side connection.</summary>
    public async Task<(bool Exists, int DatabaseId, string UserAccess)> TargetCatalogAsync()
    {
        await using SqlConnection connection = await AdminConnection().OpenMaintenanceAsync(default);
        await using SqlCommand command = new(
            "SELECT d.database_id, d.user_access_desc FROM sys.databases d WHERE d.name = @name;",
            connection
        );
        command.Parameters.AddWithValue("@name", Target.Value);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (true, reader.GetInt32(0), reader.GetString(1)) : (false, 0, "");
    }

    /// <summary>The <c>SourceIdentity</c> of the database under the target name, read on a fresh connection.</summary>
    public async Task<Guid> TargetIdentityAsync() =>
        Guid.Parse(
            await new TestDatabase(Target.Value).ScalarAsync<string>(
                "SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
            )
        );

    private static DmsTargetDatabaseName TargetName(string purpose) =>
        DmsTargetDatabaseName.TryCreate(
            $"dms1438_{purpose}_{DmsOperationId.New().Value}",
            _protectedNames,
            out DmsTargetDatabaseName name
        ) == DmsTargetNameValidation.Valid
            ? name
            : throw new InvalidOperationException("The target name does not satisfy the rules.");

    /// <summary>
    /// A deployment whose package carries <paramref name="artifact"/>, a backup of the fixture or of a variant of it, over
    /// <paramref name="engine"/> (the lane's admin engine unless a fixture installs a seam).
    /// </summary>
    public static async Task<Lane> BuildAsync(
        byte[] artifact,
        LaneHooks hooks,
        MssqlDmsTargetEngine? engine = null
    )
    {
        (string root, string operations) = NewRoot();
        Deployment deployment = await DeployAsync(root, operations, artifact, null, hooks, engine);
        return new Lane(
            root,
            operations,
            hooks,
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
    /// ephemeral key into the lane's catalog directory, <c>Compatibility</c> taken from its manifest, and
    /// <paramref name="engine"/> when given (a test holding the restore), otherwise the admin engine with
    /// <paramref name="restoreTimeout"/> as its restore bound (the 4.3 default unless given), which is also the
    /// deployment's <c>RestoreTimeout</c>.
    /// </summary>
    public static async Task<Lane> BuildAsync(
        RealTemplatePackage package,
        DmsDatabaseTemplate template,
        LaneHooks hooks,
        MssqlDmsTargetEngine? engine = null,
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
            engine,
            restoreTimeout
        );
        return new Lane(
            root,
            operations,
            hooks,
            deployment,
            new DmsDataStoreCreateRequest(template, TargetName("real"), Guid.NewGuid(), deployment.Pin)
        );
    }

    private static (string Root, string Operations) NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dms1438-mssql-lane-" + DmsOperationId.New().Value);
        string operations = Path.Combine(root, "operations");
        PrivateTestDirectory.Create(operations);
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
    /// The lane's own deployment (same options, catalog, allowlisted package and hooks, so the lane's request and pin
    /// apply) over <paramref name="inner"/> instead of the admin engine, behind a fresh recorder, with the orchestrator
    /// logging to <paramref name="logger"/> when one is given.
    /// </summary>
    public Deployment WithEngine(
        MssqlDmsTargetEngine inner,
        ILogger<DmsDataStoreProvisioningService>? logger = null
    )
    {
        RecordingEngine engine = new(inner);
        return _deployment with
        {
            Engine = engine,
            Service = ServiceFor(engine, _deployment.Catalog, _deployment.Options, _hooks, logger),
        };
    }

    /// <summary>
    /// The deployment after its allowlist was switched to another package carrying <paramref name="artifact"/> under
    /// <paramref name="packageVersion"/>, with the same manifest content, in its own catalog directory; it shares the
    /// lane's server, operations root and hooks, so it addresses the same target.
    /// </summary>
    public Task<Deployment> SwitchAllowlistAsync(byte[] artifact, string packageVersion) =>
        DeployAsync(_root, _operations, artifact, packageVersion, _hooks, null);

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
            null,
            null
        );

    private static async Task<Deployment> DeployRealAsync(
        string root,
        string operations,
        RealTemplatePackage package,
        DmsDatabaseTemplate template,
        DmsDataStoreProvisioningOptions.CompatibilityExpectations compatibility,
        DmsTrustedArtifactPin? pin,
        LaneHooks hooks,
        MssqlDmsTargetEngine? engine,
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
            engine ?? MssqlProvisioningTestServer.Engine(restoreTimeout: restoreTimeout),
            restoreTimeout,
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
        byte[] artifact,
        string? packageVersion,
        LaneHooks hooks,
        MssqlDmsTargetEngine? engine
    )
    {
        string artifacts = Path.Combine(root, "artifacts-" + DmsOperationId.New().Value);
        Directory.CreateDirectory(artifacts);
        int compatibilityLevel = await ScalarOnServerAsync<byte>(
            "SELECT compatibility_level FROM sys.databases WHERE name = N'model';"
        );
        int engineMajor = await ScalarOnServerAsync<int>(
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));"
        );

        TestTemplatePackageBuilder builder = new(mssql: true);
        if (packageVersion is not null)
        {
            builder.WithPackageVersion(packageVersion);
        }

        TestTemplatePackage package = builder
            .WithArtifact(artifact)
            .WithManifest(manifest =>
            {
                manifest["inventory"] = JsonNode.Parse(
                    FixtureText("dms-shaped-database.inventory.canonical.json")
                );
                manifest["inventorySha256"] = FixtureText("dms-shaped-database.inventory.sha256");
                manifest["projects"] = new JsonArray("edfi");
                manifest["engineVersion"] = $"{engineMajor}.0";
                manifest["databaseCompatibilityLevel"] = compatibilityLevel;
                manifest["relationalMappingVersion"] = "v3";
                manifest["effectiveSchemaHash"] = MssqlDmsShapedManifest.EffectiveSchemaHash;
                manifest["resourceKeyCount"] = MssqlDmsShapedManifest.ResourceKeyCount;
                manifest["resourceKeySeedHashB64"] = MssqlDmsShapedManifest.ResourceKeySeedHashB64;
                manifest["apiSchemaFormatVersion"] = MssqlDmsShapedManifest.ApiSchemaFormatVersion;
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
                EffectiveSchemaHash = MssqlDmsShapedManifest.EffectiveSchemaHash,
                Projects = ["edfi"],
            },
            operations,
            hooks,
            engine ?? MssqlProvisioningTestServer.Engine(),
            null,
            null
        );
    }

    /// <summary>
    /// The options, real catalog, recorded <paramref name="engine"/> and orchestrator over
    /// <paramref name="allowlisted"/>; the pin is <paramref name="pin"/>, or the one the catalog resolves now, which
    /// must succeed.
    /// </summary>
    private static async Task<Deployment> DeployOverAsync(
        Allowlisted allowlisted,
        DmsDataStoreProvisioningOptions.CompatibilityExpectations compatibility,
        string operations,
        LaneHooks hooks,
        MssqlDmsTargetEngine engine,
        TimeSpan? restoreTimeout,
        DmsTrustedArtifactPin? pin
    )
    {
        DmsDataStoreProvisioningOptions options = new()
        {
            Enabled = true,
            Provider = DmsDataStoreProvisioningOptions.MssqlProvider,
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
        RecordingEngine recording = new(engine);
        DmsDataStoreProvisioningService service = ServiceFor(recording, catalog, options, hooks);
        DmsTrustedArtifactPin resolved =
            pin
            ?? (await catalog.ResolveAsync(allowlisted.Template, default))
                .Should()
                .BeOfType<DmsArtifactResolution.Resolved>()
                .Subject.Pin;
        return new Deployment(recording, service, resolved, options, catalog, allowlisted.PackagePath);
    }

    private static DmsDataStoreProvisioningService ServiceFor(
        RecordingEngine engine,
        TemplateArtifactCatalog catalog,
        DmsDataStoreProvisioningOptions options,
        LaneHooks hooks,
        ILogger<DmsDataStoreProvisioningService>? logger = null
    ) =>
        new(
            new Ready(),
            engine,
            catalog,
            Options.Create(options),
            _protectedNames,
            AdminConnectionSource(),
            TimeProvider.System,
            logger ?? NullLogger<DmsDataStoreProvisioningService>.Instance
        )
        {
            TestHooks = hooks,
        };

    /// <summary>The orchestrator's connection source over the lane's admin connection string, which holds no secret reference.</summary>
    private static IDmsAdminConnectionSource AdminConnectionSource()
    {
        IOptions<SecretsOptions> secrets = Options.Create(new SecretsOptions());
        return new DmsAdminConnectionSource(
            Options.Create(
                new DmsDataStoreProvisioningOptions { AdminConnectionString = AdminConnectionString }
            ),
            new SecretValueCache(secrets),
            secrets,
            TimeProvider.System
        );
    }

    public async ValueTask DisposeAsync()
    {
        await new TestDatabase(Target.Value).DisposeAsync();
        if (Engine.CountOf(nameof(IDmsTargetEngine.CreateStagingAsync)) > 0)
        {
            await new TestDatabase(Operation.StagingDatabaseName.Value).DisposeAsync();
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
