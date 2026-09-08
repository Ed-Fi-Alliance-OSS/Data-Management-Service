// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Tests.Integration.Tests.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCache")]
[Category("DocumentCacheAcceptance")]
public class Given_DocumentCache_Target_Resolution_Composition
{
    private static readonly DocumentCacheTargetKey _defaultTargetKey = DocumentCacheTargetKey.Create("", 1);

    private static readonly DocumentCacheTargetKey _tenantTargetKey = DocumentCacheTargetKey.Create(
        "TenantA",
        7
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly byte[] _resourceKeySeedHash =
    [
        0,
        1,
        2,
        3,
        4,
        5,
        6,
        7,
        8,
        9,
        10,
        11,
        12,
        13,
        14,
        15,
        16,
        17,
        18,
        19,
        20,
        21,
        22,
        23,
        24,
        25,
        26,
        27,
        28,
        29,
        30,
        31,
    ];

    private static readonly EffectiveSchemaSet _effectiveSchemaSet = new(
        new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "5.2.0",
            RelationalMappingVersion: "relational-v1",
            EffectiveSchemaHash: "schema-hash",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: _resourceKeySeedHash,
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(
                    ResourceKeyId: 1,
                    Resource: new QualifiedResourceName("Ed-Fi", "Student"),
                    ResourceVersion: "5.2.0",
                    IsAbstractResource: false
                ),
            ]
        ),
        ProjectsInEndpointOrder: []
    );

    private static readonly DatabaseFingerprint _databaseFingerprint = new(
        ApiSchemaFormatVersion: "5.2.0",
        EffectiveSchemaHash: "schema-hash",
        ResourceKeyCount: 1,
        ResourceKeySeedHash: [.. _resourceKeySeedHash]
    );

    private static readonly DocumentCacheLifecycleObservation _disabledLifecycle = new(
        DocumentCacheLifecycleState.Disabled,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheLifecycleObservation _trackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheInventoryValidationResult _satisfiedInventory = new(
        DocumentCacheInventoryStatus.Satisfied,
        "Inventory satisfied."
    );

    private static readonly DocumentCacheEnqueueTriggerValidationResult _satisfiedEnqueueTrigger = new(
        DocumentCacheEnqueueTriggerStatus.Satisfied,
        "Enqueue trigger satisfied."
    );

    [Test]
    public async Task It_marks_resolution_ineligibility_categories_without_hiding_peer_targets()
    {
        using CompositionFixture fixture = CompositionFixture.Create(
            RelationalProviderToken.Postgresql,
            CreateOptions([("", 1), ("", 2), ("", 3), ("", 4), ("", 5), ("", 6)])
        );
        fixture.ProviderAdapter.SetObservation("eligible-connection");
        fixture.ProviderAdapter.SetObservation(
            "invalid-inventory-connection",
            inventory: InvalidInventory("Required DocumentCache inventory is missing.")
        );
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "eligible-connection", RelationalProviderToken.Postgresql),
            CreateDataStore(2, "  ", RelationalProviderToken.Postgresql),
            CreateDataStore(
                3,
                "missing-provider-connection",
                relationalProviderToken: null,
                RelationalProviderMetadataStatus.Missing
            ),
            CreateDataStore(
                4,
                "unknown-provider-connection",
                relationalProviderToken: null,
                RelationalProviderMetadataStatus.Unknown
            ),
            CreateDataStore(5, "sqlserver-connection", RelationalProviderToken.SqlServer),
            CreateDataStore(6, "invalid-inventory-connection", RelationalProviderToken.Postgresql)
        );

        await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheDiagnosticSnapshot diagnostics = fixture.Diagnostics.CurrentSnapshot;
        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = fixture.Registry.CurrentRuntimeSnapshot;

        diagnostics.Targets.Select(target => target.TargetKey.DataStoreId).Should().Equal(1, 2, 3, 4, 5, 6);
        diagnostics
            .Targets.Single(target => target.TargetKey.DataStoreId == 1)
            .EligibilityState.Should()
            .Be(DocumentCacheTargetEligibilityState.Eligible);
        diagnostics
            .Targets.Single(target => target.TargetKey.DataStoreId == 1)
            .EffectiveSettings.ReadAccelerationEnabled.Should()
            .BeTrue();
        diagnostics
            .Targets.Single(target => target.TargetKey.DataStoreId == 1)
            .EffectiveSettings.ProjectorPageSize.Should()
            .Be(25);
        runtimeSnapshot.ExecutionContexts.Select(context => context.TargetKey.DataStoreId).Should().Equal(1);
        runtimeSnapshot
            .GetExecutionContext(_defaultTargetKey)!
            .ConnectionInput.Value.Should()
            .Be("eligible-connection");
        runtimeSnapshot.GetExecutionContext(DocumentCacheTargetKey.Create("", 2)).Should().BeNull();

        AssertSingleDiagnosticCategory(
            diagnostics,
            dataStoreId: 2,
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing
        );
        AssertSingleDiagnosticCategory(
            diagnostics,
            dataStoreId: 3,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing
        );
        AssertSingleDiagnosticCategory(
            diagnostics,
            dataStoreId: 4,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown
        );
        AssertSingleDiagnosticCategory(
            diagnostics,
            dataStoreId: 5,
            DocumentCacheTargetDiagnosticCategory.ProviderMismatch
        );
        AssertSingleDiagnosticCategory(
            diagnostics,
            dataStoreId: 6,
            DocumentCacheTargetDiagnosticCategory.InventoryFailure
        );
        AssertDiagnosticMessagesDoNotLeakPhysicalDetails(diagnostics);
    }

    [Test]
    public async Task It_does_not_expose_execution_contexts_for_targets_with_mismatched_schema_metadata()
    {
        using CompositionFixture fixture = CompositionFixture.Create(
            RelationalProviderToken.Postgresql,
            CreateOptions([("", 1), ("", 2)])
        );
        fixture.ProviderAdapter.SetObservation("eligible-connection");
        fixture.ProviderAdapter.SetObservation(
            "schema-mismatch-connection",
            databaseFingerprint: _databaseFingerprint with
            {
                EffectiveSchemaHash = "other-schema-hash",
            }
        );
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "eligible-connection", RelationalProviderToken.Postgresql),
            CreateDataStore(2, "schema-mismatch-connection", RelationalProviderToken.Postgresql)
        );

        await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);

        DocumentCacheTargetRuntimeSnapshot runtimeSnapshot = fixture.Registry.CurrentRuntimeSnapshot;
        DocumentCacheDiagnosticSnapshot diagnostics = fixture.Diagnostics.CurrentSnapshot;

        runtimeSnapshot.ExecutionContexts.Select(context => context.TargetKey.DataStoreId).Should().Equal(1);
        runtimeSnapshot.GetExecutionContext(DocumentCacheTargetKey.Create("", 2)).Should().BeNull();
        AssertSingleDiagnosticCategory(
            diagnostics,
            dataStoreId: 2,
            DocumentCacheTargetDiagnosticCategory.EffectiveSchemaCompatibilityFailure
        );
        AssertDiagnosticMessagesDoNotLeakPhysicalDetails(diagnostics);
    }

    [Test]
    public async Task It_treats_CMS_refresh_failures_as_operational_and_resolves_later_without_expanding_membership()
    {
        DocumentCacheOptions options = CreateOptions([("", 1), ("TenantA", 7)]);
        using CompositionFixture fixture = CompositionFixture.Create(
            RelationalProviderToken.Postgresql,
            options
        );
        fixture.ProviderAdapter.SetObservation("default-connection");
        fixture.ProviderAdapter.SetObservation("tenant-connection");

        options.Targets.Add(new DocumentCacheTargetOptions { TenantKey = "TenantB", DataStoreId = 9 });
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "default-connection", RelationalProviderToken.Postgresql)
        );
        fixture.DataStoreProvider.QueueLoadFailure(
            "TenantA",
            new InvalidOperationException("CMS unavailable for Host=prod.example Database=secret")
        );

        DocumentCacheTargetRegistrySnapshot failedRefresh = await fixture.Registry.RefreshAsync(
            DocumentCacheTargetRefreshReason.CmsRefreshNotification
        );

        failedRefresh
            .Targets.Select(target => target.TargetKey)
            .Should()
            .Equal(_defaultTargetKey, _tenantTargetKey);
        failedRefresh
            .Targets.Single(target => target.TargetKey.Equals(_defaultTargetKey))
            .EligibilityState.Should()
            .Be(DocumentCacheTargetEligibilityState.Eligible);
        failedRefresh
            .Targets.Single(target => target.TargetKey.Equals(_tenantTargetKey))
            .Diagnostics.Should()
            .ContainSingle()
            .Which.Category.Should()
            .Be(DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure);
        fixture
            .Registry.CurrentRuntimeSnapshot.ExecutionContexts.Select(context => context.TargetKey)
            .Should()
            .Equal(_defaultTargetKey);

        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "default-connection", RelationalProviderToken.Postgresql)
        );
        fixture.DataStoreProvider.QueueLoadResult(
            "TenantA",
            CreateDataStore(7, "tenant-connection", RelationalProviderToken.Postgresql)
        );

        DocumentCacheTargetRegistrySnapshot resolvedRefresh = await fixture.Registry.RefreshAsync(
            DocumentCacheTargetRefreshReason.CmsRefreshNotification
        );

        resolvedRefresh
            .Targets.Select(target => target.TargetKey)
            .Should()
            .Equal(_defaultTargetKey, _tenantTargetKey);
        resolvedRefresh
            .Targets.Single(target => target.TargetKey.Equals(_tenantTargetKey))
            .EligibilityState.Should()
            .Be(DocumentCacheTargetEligibilityState.Eligible);
        fixture
            .Registry.CurrentRuntimeSnapshot.ExecutionContexts.Select(context => context.TargetKey)
            .Should()
            .Equal(_defaultTargetKey, _tenantTargetKey);
        fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("", "TenantA", "", "TenantA");
        fixture.DataStoreProvider.LoadTenantsCallCount.Should().Be(0);
        fixture.DataStoreProvider.TenantExistsCallCount.Should().Be(0);
        fixture.DataStoreProvider.GetLoadedTenantKeysCallCount.Should().Be(0);
        fixture
            .Diagnostics.CurrentSnapshot.Targets.Should()
            .NotContain(target => target.TargetKey.Equals(DocumentCacheTargetKey.Create("TenantB", 9)));
        AssertDiagnosticMessagesDoNotLeakPhysicalDetails(fixture.Diagnostics.CurrentSnapshot);
    }

    [Test]
    public async Task It_keeps_failed_initialization_ineligible_until_replacement_context_is_created()
    {
        using CompositionFixture fixture = CompositionFixture.Create(
            RelationalProviderToken.SqlServer,
            CreateOptions([("", 1)])
        );
        fixture.ProviderAdapter.SetObservation(
            "same-connection",
            lifecycle: _disabledLifecycle,
            prerequisites: lifecycle =>
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    DisabledReadCommittedSnapshotPrerequisites(),
                    lifecycle
                )
        );
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "same-connection", RelationalProviderToken.SqlServer)
        );

        DocumentCacheTargetRegistrySnapshot firstRefresh = await fixture.Registry.RefreshAsync(
            DocumentCacheTargetRefreshReason.Startup
        );

        DocumentCacheTargetObservation failedObservation = firstRefresh.Targets.Single();
        failedObservation.Generation!.Value.Should().Be(1);
        failedObservation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
        failedObservation
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed
            );
        fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_defaultTargetKey).Should().BeNull();

        fixture.ProviderAdapter.SetObservation(
            "same-connection",
            lifecycle: _disabledLifecycle,
            prerequisites: lifecycle =>
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    SatisfiedSqlServerPrerequisites(),
                    lifecycle
                )
        );
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "same-connection", RelationalProviderToken.SqlServer)
        );

        DocumentCacheTargetRegistrySnapshot sameSignatureRefresh = await fixture.Registry.RefreshAsync(
            DocumentCacheTargetRefreshReason.CmsRefreshNotification
        );

        sameSignatureRefresh.Targets.Single().Generation!.Value.Should().Be(1);
        sameSignatureRefresh
            .Targets.Single()
            .EligibilityState.Should()
            .Be(DocumentCacheTargetEligibilityState.Ineligible);
        fixture.Registry.CurrentRuntimeSnapshot.GetExecutionContext(_defaultTargetKey).Should().BeNull();
        fixture.ProviderAdapter.InitializationPrerequisiteCallCount("same-connection").Should().Be(1);

        fixture.ProviderAdapter.SetObservation(
            "replacement-connection",
            lifecycle: _disabledLifecycle,
            prerequisites: lifecycle =>
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    SatisfiedSqlServerPrerequisites(),
                    lifecycle
                )
        );
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "replacement-connection", RelationalProviderToken.SqlServer)
        );

        DocumentCacheTargetRegistrySnapshot replacementRefresh = await fixture.Registry.RefreshAsync(
            DocumentCacheTargetRefreshReason.SupervisorTriggered
        );

        DocumentCacheTargetObservation replacementObservation = replacementRefresh.Targets.Single();
        replacementObservation.Generation!.Value.Should().Be(2);
        replacementObservation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
        replacementObservation
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TargetReplaced
            );
        fixture
            .Registry.CurrentRuntimeSnapshot.GetExecutionContext(
                _defaultTargetKey,
                new DocumentCacheTargetContextGeneration(2)
            )!
            .ConnectionInput.Value.Should()
            .Be("replacement-connection");
        fixture.ProviderAdapter.InitializationPrerequisiteCallCount("replacement-connection").Should().Be(1);
    }

    [Test]
    public async Task It_classifies_prerequisite_failures_with_non_disabled_lifecycle_as_unsupported_incidents()
    {
        using CompositionFixture fixture = CompositionFixture.Create(
            RelationalProviderToken.SqlServer,
            CreateOptions([("", 1)])
        );
        fixture.ProviderAdapter.SetObservation(
            "tracking-prerequisite-failure",
            lifecycle: _trackingLifecycle,
            prerequisites: lifecycle =>
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    DisabledReadCommittedSnapshotPrerequisites(),
                    lifecycle
                )
        );
        fixture.DataStoreProvider.QueueLoadResult(
            tenant: null,
            CreateDataStore(1, "tracking-prerequisite-failure", RelationalProviderToken.SqlServer)
        );

        DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
            DocumentCacheTargetRefreshReason.Startup
        );

        DocumentCacheTargetObservation observation = snapshot.Targets.Single();
        observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
        observation.Lifecycle!.State.Should().Be(DocumentCacheLifecycleState.Tracking);
        observation
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident
            );
    }

    private static void AssertSingleDiagnosticCategory(
        DocumentCacheDiagnosticSnapshot diagnostics,
        long dataStoreId,
        DocumentCacheTargetDiagnosticCategory category
    )
    {
        DocumentCacheTargetDiagnosticSnapshot target = diagnostics.Targets.Single(target =>
            target.TargetKey.DataStoreId == dataStoreId
        );

        target.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
        target.Diagnostics.Should().ContainSingle().Which.Category.Should().Be(category);
    }

    private static void AssertDiagnosticMessagesDoNotLeakPhysicalDetails(
        DocumentCacheDiagnosticSnapshot diagnostics
    )
    {
        IReadOnlyList<string> messages =
        [
            .. diagnostics
                .Targets.SelectMany(target => target.Diagnostics)
                .Select(diagnostic => diagnostic.Message),
        ];

        messages
            .Should()
            .NotContain(message => message.Contains("Host=", StringComparison.OrdinalIgnoreCase));
        messages
            .Should()
            .NotContain(message => message.Contains("Database=", StringComparison.OrdinalIgnoreCase));
        messages
            .Should()
            .NotContain(message => message.Contains("Password=", StringComparison.OrdinalIgnoreCase));
        messages
            .Should()
            .NotContain(message => message.Contains("secret", StringComparison.OrdinalIgnoreCase));
        messages
            .Should()
            .NotContain(message => message.Contains("prod.example", StringComparison.OrdinalIgnoreCase));
    }

    private static DocumentCacheOptions CreateOptions(
        IReadOnlyList<(string TenantKey, long DataStoreId)> targets
    ) =>
        new()
        {
            Targets = targets
                .Select(target => new DocumentCacheTargetOptions
                {
                    TenantKey = target.TenantKey,
                    DataStoreId = target.DataStoreId,
                })
                .ToList(),
            ReadAcceleration = new DocumentCacheReadAccelerationOptions { Enabled = true },
            Projector = new DocumentCacheProjectorOptions
            {
                PageSize = 25,
                FailureBackoff = TimeSpan.FromSeconds(10),
            },
        };

    private static DataStore CreateDataStore(
        long id,
        string? connectionString,
        RelationalProviderToken? relationalProviderToken,
        RelationalProviderMetadataStatus relationalProviderMetadataStatus =
            RelationalProviderMetadataStatus.Supported
    ) =>
        new(
            id,
            "Operational",
            "Display Host=prod.example Database=secret Password=hidden",
            connectionString,
            new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("schoolYear")] = new("2026"),
            },
            relationalProviderToken,
            relationalProviderMetadataStatus
        );

    private static DocumentCacheProviderInventoryValidationResult InvalidInventory(string message) =>
        new(
            new DocumentCacheInventoryValidationResult(DocumentCacheInventoryStatus.Invalid, message),
            _satisfiedEnqueueTrigger
        );

    private static DocumentCacheSqlServerPrerequisiteDetails SatisfiedSqlServerPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "Read committed snapshot satisfied."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "Nested triggers satisfied."
            )
        );

    private static DocumentCacheSqlServerPrerequisiteDetails DisabledReadCommittedSnapshotPrerequisites() =>
        new(
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                DocumentCacheProviderPrerequisiteStatus.Disabled,
                "Read committed snapshot is disabled."
            ),
            new DocumentCacheProviderPrerequisiteResult(
                DocumentCacheProviderPrerequisiteName.NestedTriggers,
                DocumentCacheProviderPrerequisiteStatus.Satisfied,
                "Nested triggers satisfied."
            )
        );

    private sealed class CompositionFixture : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public SequencedDataStoreProvider DataStoreProvider { get; } = new();

        public ScriptedDocumentCacheProviderAdapter ProviderAdapter { get; }

        public IDocumentCacheTargetRegistry Registry { get; }

        public IDocumentCacheDiagnosticSnapshotProvider Diagnostics { get; }

        private CompositionFixture(RelationalProviderToken processProviderToken, DocumentCacheOptions options)
        {
            ProviderAdapter = new ScriptedDocumentCacheProviderAdapter(processProviderToken);

            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton<IDataStoreProvider>(DataStoreProvider);
            services.AddSingleton<IOptions<DocumentCacheOptions>>(Options.Create(options));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new DocumentCacheProcessProviderToken(processProviderToken));
            services.AddSingleton<IDocumentCachePhysicalSourceFingerprintReader>(ProviderAdapter);
            services.AddSingleton<IDocumentCacheLifecycleReader>(ProviderAdapter);
            services.AddSingleton<IDocumentCacheInventoryValidator>(ProviderAdapter);
            services.AddSingleton<IDocumentCacheProviderPrerequisiteValidator>(ProviderAdapter);
            services.AddSingleton<IEffectiveSchemaSetProvider>(
                new StaticEffectiveSchemaSetProvider(_effectiveSchemaSet)
            );
            services.AddSingleton<IDatabaseFingerprintReader>(ProviderAdapter);
            services.AddSingleton<IResourceKeyValidator>(ProviderAdapter);
            services.AddSingleton<IDocumentCacheTargetContextBuilder, DocumentCacheTargetContextBuilder>();
            services.AddSingleton<IDocumentCacheTargetRegistry, DocumentCacheTargetRegistry>();
            services.AddSingleton<
                IDocumentCacheDiagnosticSnapshotProvider,
                DocumentCacheDiagnosticSnapshotProvider
            >();

            _serviceProvider = services.BuildServiceProvider();
            Registry = _serviceProvider.GetRequiredService<IDocumentCacheTargetRegistry>();
            Diagnostics = _serviceProvider.GetRequiredService<IDocumentCacheDiagnosticSnapshotProvider>();
        }

        public static CompositionFixture Create(
            RelationalProviderToken processProviderToken,
            DocumentCacheOptions options
        ) => new(processProviderToken, options);

        public void Dispose() => _serviceProvider.Dispose();
    }

    private sealed class ScriptedDocumentCacheProviderAdapter(RelationalProviderToken providerToken)
        : IDocumentCachePhysicalSourceFingerprintReader,
            IDatabaseFingerprintReader,
            IResourceKeyValidator,
            IDocumentCacheLifecycleReader,
            IDocumentCacheInventoryValidator,
            IDocumentCacheProviderPrerequisiteValidator
    {
        private readonly Dictionary<string, ProviderObservation> _observations = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, int> _initializationPrerequisiteCallCounts = new(
            StringComparer.OrdinalIgnoreCase
        );

        public RelationalProviderToken ProviderToken { get; } = providerToken;

        public void SetObservation(
            string connectionString,
            DocumentCacheLifecycleObservation? lifecycle = null,
            DocumentCacheProviderInventoryValidationResult? inventory = null,
            DatabaseFingerprint? databaseFingerprint = null,
            ResourceKeyValidationResult? resourceKeyValidation = null,
            Func<
                DocumentCacheLifecycleObservation,
                DocumentCacheProviderPrerequisiteValidationResult
            >? prerequisites = null
        )
        {
            DocumentCacheLifecycleObservation effectiveLifecycle = lifecycle ?? _trackingLifecycle;
            _observations[connectionString] = new ProviderObservation(
                DocumentCachePhysicalSourceFingerprintReadResult.Success(_fingerprint),
                DocumentCacheLifecycleReadResult.Success(effectiveLifecycle),
                inventory
                    ?? new DocumentCacheProviderInventoryValidationResult(
                        _satisfiedInventory,
                        _satisfiedEnqueueTrigger
                    ),
                databaseFingerprint ?? _databaseFingerprint,
                resourceKeyValidation ?? new ResourceKeyValidationResult.ValidationSuccess(),
                prerequisites
                    ?? (
                        observedLifecycle =>
                            DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                                ProviderToken == RelationalProviderToken.SqlServer
                                    ? SatisfiedSqlServerPrerequisites()
                                    : DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
                                observedLifecycle
                            )
                    )
            );
        }

        public int InitializationPrerequisiteCallCount(string connectionString) =>
            _initializationPrerequisiteCallCounts.GetValueOrDefault(connectionString);

        public Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadFingerprintAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetObservation(connectionString).FingerprintResult);
        }

        public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString) =>
            Task.FromResult<DatabaseFingerprint?>(GetObservation(connectionString).DatabaseFingerprint);

        public Task<ResourceKeyValidationResult> ValidateAsync(
            DatabaseFingerprint dbFingerprint,
            short expectedResourceKeyCount,
            ImmutableArray<byte> expectedResourceKeySeedHash,
            IReadOnlyList<ResourceKeyRow> expectedResourceKeysInIdOrder,
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetObservation(connectionString).ResourceKeyValidation);
        }

        public Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetObservation(connectionString).LifecycleResult);
        }

        public Task<DocumentCacheProviderInventoryValidationResult> ValidateInventoryAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetObservation(connectionString).InventoryResult);
        }

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateInitializationAsync(
            string connectionString,
            DocumentCacheLifecycleObservation lifecycle,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _initializationPrerequisiteCallCounts[connectionString] =
                InitializationPrerequisiteCallCount(connectionString) + 1;

            return Task.FromResult(GetObservation(connectionString).Prerequisites(lifecycle));
        }

        public Task<DocumentCacheProviderPrerequisiteValidationResult> ValidateActivationPreflightAsync(
            string connectionString,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentCacheLifecycleObservation lifecycle =
                GetObservation(connectionString).LifecycleResult.Lifecycle
                ?? new DocumentCacheLifecycleObservation(
                    DocumentCacheLifecycleState.Disabled,
                    CacheAheadRecoveryRequired: false
                );

            return Task.FromResult(GetObservation(connectionString).Prerequisites(lifecycle));
        }

        private ProviderObservation GetObservation(string connectionString) =>
            _observations.TryGetValue(connectionString, out ProviderObservation? observation)
                ? observation
                : throw new InvalidOperationException(
                    $"No scripted DocumentCache provider observation exists for '{connectionString}'."
                );
    }

    private sealed record ProviderObservation(
        DocumentCachePhysicalSourceFingerprintReadResult FingerprintResult,
        DocumentCacheLifecycleReadResult LifecycleResult,
        DocumentCacheProviderInventoryValidationResult InventoryResult,
        DatabaseFingerprint DatabaseFingerprint,
        ResourceKeyValidationResult ResourceKeyValidation,
        Func<
            DocumentCacheLifecycleObservation,
            DocumentCacheProviderPrerequisiteValidationResult
        > Prerequisites
    );

    private sealed class StaticEffectiveSchemaSetProvider(EffectiveSchemaSet effectiveSchemaSet)
        : IEffectiveSchemaSetProvider
    {
        public EffectiveSchemaSet EffectiveSchemaSet { get; } = effectiveSchemaSet;

        public bool IsInitialized => true;

        public void Initialize(EffectiveSchemaSet effectiveSchemaSet) =>
            throw new InvalidOperationException("Static test provider is already initialized.");
    }

    private sealed class SequencedDataStoreProvider : IDataStoreProvider
    {
        private readonly Dictionary<string, Queue<LoadDataStoresResult>> _queuedLoadResults = new(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, IList<DataStore>> _loadedDataStores = new(
            StringComparer.OrdinalIgnoreCase
        );

        public List<string> LoadDataStoreCalls { get; } = [];

        public int LoadTenantsCallCount { get; private set; }

        public int TenantExistsCallCount { get; private set; }

        public int GetLoadedTenantKeysCallCount { get; private set; }

        public void QueueLoadResult(string? tenant, params DataStore[] dataStores) =>
            GetQueue(tenant).Enqueue(LoadDataStoresResult.Success(dataStores));

        public void QueueLoadFailure(string? tenant, Exception exception) =>
            GetQueue(tenant).Enqueue(LoadDataStoresResult.Failure(exception));

        public Task<IList<DataStore>> LoadDataStores(string? tenant = null)
        {
            string tenantKey = GetTenantKey(tenant);
            LoadDataStoreCalls.Add(tenantKey);

            Queue<LoadDataStoresResult> queue = GetQueue(tenant);
            if (queue.Count == 0)
            {
                _loadedDataStores[tenantKey] = [];
                return Task.FromResult<IList<DataStore>>([]);
            }

            LoadDataStoresResult result = queue.Dequeue();
            if (result.Exception is not null)
            {
                throw result.Exception;
            }

            _loadedDataStores[tenantKey] = result.DataStores;
            return Task.FromResult(result.DataStores);
        }

        public Task RefreshInstancesIfExpiredAsync(string? tenant = null) =>
            throw new AssertionException("DocumentCache target registry must use explicit load hooks.");

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
            _loadedDataStores.TryGetValue(GetTenantKey(tenant), out IList<DataStore>? dataStores)
                ? dataStores.ToList().AsReadOnly()
                : [];

        public DataStore? GetById(long id, string? tenant = null) =>
            _loadedDataStores.TryGetValue(GetTenantKey(tenant), out IList<DataStore>? dataStores)
                ? dataStores.FirstOrDefault(dataStore => dataStore.Id == id)
                : null;

        public bool IsLoaded(string? tenant = null) => _loadedDataStores.ContainsKey(GetTenantKey(tenant));

        public Task<IList<string>> LoadTenants()
        {
            LoadTenantsCallCount++;
            return Task.FromResult<IList<string>>([]);
        }

        public bool TenantExists(string tenant)
        {
            TenantExistsCallCount++;
            return false;
        }

        public IReadOnlyList<string> GetLoadedTenantKeys()
        {
            GetLoadedTenantKeysCallCount++;
            return [];
        }

        private Queue<LoadDataStoresResult> GetQueue(string? tenant)
        {
            string tenantKey = GetTenantKey(tenant);
            if (!_queuedLoadResults.TryGetValue(tenantKey, out Queue<LoadDataStoresResult>? queue))
            {
                queue = new Queue<LoadDataStoresResult>();
                _queuedLoadResults.Add(tenantKey, queue);
            }

            return queue;
        }

        private static string GetTenantKey(string? tenant) => tenant ?? string.Empty;
    }

    private sealed record LoadDataStoresResult(IList<DataStore> DataStores, Exception? Exception)
    {
        public static LoadDataStoresResult Success(IList<DataStore> dataStores) =>
            new(dataStores, Exception: null);

        public static LoadDataStoresResult Failure(Exception exception) => new([], exception);
    }
}
