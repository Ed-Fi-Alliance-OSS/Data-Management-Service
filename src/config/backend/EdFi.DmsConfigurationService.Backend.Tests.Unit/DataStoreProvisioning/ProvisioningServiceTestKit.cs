// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

/// <summary>The values and request builders the orchestrator fixtures share.</summary>
internal static class ProvisioningTestValues
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public static readonly ProtectedDatabaseNames ProtectedNames = ProtectedDatabaseNames.Create(
        "edfi_configurationservice",
        []
    );

    public static readonly DmsServerSessionId Session = new(
        7,
        new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)
    );

    public static DmsTargetDatabaseName Name(string value, ProtectedDatabaseNames? protectedNames = null)
    {
        DmsTargetDatabaseName
            .TryCreate(value, protectedNames ?? ProtectedNames, out DmsTargetDatabaseName name)
            .Should()
            .Be(DmsTargetNameValidation.Valid);
        return name;
    }

    /// <summary>A pin that names no staged package: a request carrying it differs from what the catalog stages.</summary>
    public static DmsTrustedArtifactPin Pin(string packageSha256 = "a") =>
        new()
        {
            ContractVersion = 1,
            Provider = "postgresql",
            PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
            PackageVersion = "1.0.335",
            PackageSha256 = new string(packageSha256[0], 64),
            ArtifactSha256 = new string('b', 64),
            TemplateKind = "Minimal",
            DataStandardVersion = "5.2.0",
            EffectiveSchemaHash = new string('c', 64),
            InventorySha256 = new string('d', 64),
            ApiSchemaFormatVersion = "1.0.0",
            RelationalMappingVersion = "v3",
        };

    /// <summary>A create of the Minimal template carrying the shared catalog's real pin unless told otherwise.</summary>
    public static DmsDataStoreCreateRequest CreateRequest(
        DmsTargetDatabaseName? name = null,
        DmsTrustedArtifactPin? pin = null,
        Guid? expectedSourceIdentity = null
    ) =>
        new(
            DmsDatabaseTemplate.Minimal,
            name ?? Name("EdFi_Ods_Test"),
            expectedSourceIdentity ?? Guid.NewGuid(),
            pin ?? SharedTemplateCatalog.Pin
        );

    public static DmsDataStoreDeleteRequest DeleteRequest(DmsTargetDatabaseName? name = null) =>
        new(name ?? Name("EdFi_Ods_Test"), Guid.NewGuid());

    public static DmsProvisioningFailure FailureOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Detail;

    public static DmsProvisioningFailure FailureOf(DmsDataStoreDeleteResult result) =>
        result.Should().BeOfType<DmsDataStoreDeleteResult.Failure>().Subject.Detail;

    public static DmsCleanupReport CleanupOf(DmsDataStoreCreateResult result) =>
        result.Should().BeOfType<DmsDataStoreCreateResult.Failure>().Subject.Cleanup;

    /// <summary>
    /// Waits for the engine read to start without hanging: if the operation completes first (because an
    /// earlier step refused it), the test fails here with a reason instead of waiting forever.
    /// </summary>
    public static async Task ReadStartedAsync(TaskCompletionSource readStarted, Task operation)
    {
        await Task.WhenAny(readStarted.Task, operation);
        readStarted
            .Task.IsCompleted.Should()
            .BeTrue("the operation must reach the preamble read instead of ending earlier");
    }
}

/// <summary>
/// The one real catalog the orchestrator fixtures stage from: a workspace whose Minimal package carries
/// a manifest describing <see cref="TargetFactsSample.PostgresqlFacts"/>, so a spy engine that returns
/// those facts validates, with the pin that package resolves to. Built on first use; the namespace
/// set-up fixture disposes it.
/// </summary>
internal static class SharedTemplateCatalog
{
    private static readonly Lazy<SharedCatalogBuild> _built = new(Build);

    public static CatalogWorkspace Workspace => _built.Value.Workspace;

    public static TemplateArtifactCatalog Catalog => _built.Value.Catalog;

    public static DmsTrustedArtifactPin Pin => _built.Value.Pin;

    public static DmsDataStoreProvisioningOptions Options => _built.Value.Options;

    public static void TearDown()
    {
        if (_built.IsValueCreated)
        {
            _built.Value.Workspace.Dispose();
        }
    }

    private static SharedCatalogBuild Build()
    {
        CatalogWorkspace workspace = new();
        RestoreManifest described = TargetFactsSample.ManifestFor(TargetFactsSample.PostgresqlFacts());
        string inventoryJson = CanonicalInventoryJson.Serialize(described.Inventory);
        workspace.Build(
            workspace
                .MinimalBuilder()
                .WithManifest(manifest =>
                {
                    manifest["inventory"] = JsonNode.Parse(inventoryJson);
                    manifest["inventorySha256"] = described.InventorySha256;
                    manifest["projects"] = new JsonArray([
                        .. TargetFactsSample.Projects.Select(project => (JsonNode)project),
                    ]);
                    manifest["engineVersion"] = described.EngineVersion;
                    manifest["relationalMappingVersion"] = described.RelationalMappingVersion;
                })
        );
        DmsDataStoreProvisioningOptions options = workspace.Options();
        options.Compatibility!.Projects = TargetFactsSample.Projects;
        TemplateArtifactCatalog catalog = workspace.Catalog(options);
        DmsArtifactResolution resolution = catalog
            .ResolveAsync(DmsDatabaseTemplate.Minimal, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        DmsTrustedArtifactPin pin = resolution
            .Should()
            .BeOfType<DmsArtifactResolution.Resolved>()
            .Subject.Pin;
        return new SharedCatalogBuild(workspace, catalog, pin, options);
    }
}

internal sealed record SharedCatalogBuild(
    CatalogWorkspace Workspace,
    TemplateArtifactCatalog Catalog,
    DmsTrustedArtifactPin Pin,
    DmsDataStoreProvisioningOptions Options
);

[SetUpFixture]
public class DataStoreProvisioningNamespaceSetUp
{
    [OneTimeTearDown]
    public void TearDown() => SharedTemplateCatalog.TearDown();
}

internal sealed class FakeReadiness(DmsProvisionerReadiness current) : IDmsDataStoreProvisionerReadiness
{
    public DmsProvisionerReadiness Current { get; } = current;
}

/// <summary>
/// A catalog whose resolution is scripted and whose staging defaults to the shared real catalog, so the
/// orchestrator holds a real staged artifact (and releases a real directory) while the engine is a spy.
/// </summary>
internal sealed class FakeCatalog : ITemplateArtifactCatalog
{
    public delegate Task<TemplateArtifactStaging> StageScript(
        DmsDatabaseTemplate template,
        DmsOperationId operationId,
        CancellationToken cancellationToken
    );

    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Func<DmsDatabaseTemplate, DmsArtifactResolution> OnResolve { get; set; } =
        _ => new DmsArtifactResolution.Resolved(SharedTemplateCatalog.Pin, DmsCleanupReport.Clean);

    public StageScript OnStage { get; set; } =
        (template, operation, token) => SharedTemplateCatalog.Catalog.StageAsync(template, operation, token);

    public Task<DmsArtifactResolution> ResolveAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(OnResolve(template));
    }

    public Task<TemplateArtifactStaging> StageAsync(
        DmsDatabaseTemplate template,
        DmsOperationId operationId,
        CancellationToken cancellationToken
    )
    {
        Interlocked.Increment(ref _calls);
        return OnStage(template, operationId, cancellationToken);
    }

    public Task<IReadOnlyList<DmsReadinessReason>> ValidateAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("The orchestrator never runs the readiness probes.");
}

/// <summary>
/// The orchestrator under test with its doubles; the engine hands out a fresh fake holder per acquisition.
/// The clock is fake unless a fixture needs the cleanup backoff to elapse on its own, in which case it is
/// the system clock and the waits are real but short.
/// </summary>
internal sealed class Harness
{
    public Harness(
        DmsProvisionerReadiness? readiness = null,
        ProtectedDatabaseNames? protectedNames = null,
        Action<DmsDataStoreProvisioningOptions>? configure = null,
        IDmsProvisioningTestHooks? hooks = null,
        bool systemClock = false
    )
    {
        TimeProvider clock = systemClock ? TimeProvider.System : Time;
        Engine.OnAcquireTargetLock = _ =>
        {
            FakeDmsTargetLockHolder holder = new(
                ProvisioningTestValues.Session,
                TimeSpan.FromSeconds(5),
                ProvisioningTestValues.CommandTimeout,
                clock
            );
            Holders.Add(holder);
            return Task.FromResult<DmsTargetLockResult>(new DmsTargetLockResult.Acquired(holder));
        };
        DmsDataStoreProvisioningOptions options = new()
        {
            CommandTimeout = ProvisioningTestValues.CommandTimeout,
            RestoreTimeout = TimeSpan.FromMinutes(1),
            SessionTerminationTimeout = TimeSpan.FromSeconds(30),
            CleanupAttempts = 1,
            Compatibility = new() { Projects = TargetFactsSample.Projects },
        };
        configure?.Invoke(options);
        Service = new DmsDataStoreProvisioningService(
            new FakeReadiness(readiness ?? Ready()),
            Engine,
            Catalog,
            Options.Create(options),
            protectedNames ?? ProvisioningTestValues.ProtectedNames,
            clock,
            NullLogger<DmsDataStoreProvisioningService>.Instance
        )
        {
            TestHooks = hooks ?? NoOpProvisioningTestHooks.Instance,
        };
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    public SpyDmsTargetEngine Engine { get; } = new();

    public FakeCatalog Catalog { get; } = new();

    public List<FakeDmsTargetLockHolder> Holders { get; } = [];

    public DmsDataStoreProvisioningService Service { get; }

    public FakeDmsTargetLockHolder OnlyHolder => Holders.Should().ContainSingle().Subject;

    private static DmsProvisionerReadiness Ready() =>
        new DmsProvisionerReadiness.Ready(
            "postgresql",
            new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero)
        );
}
