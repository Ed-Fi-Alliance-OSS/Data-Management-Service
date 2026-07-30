// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheTargetRegistry")]
public class DocumentCacheTargetRegistryTests
{
    private static readonly DocumentCacheTargetKey _defaultTargetKey = DocumentCacheTargetKey.Create(null, 1);

    private static readonly DocumentCacheTargetKey _tenantTargetKey = DocumentCacheTargetKey.Create(
        "TenantA",
        7
    );

    private static readonly DocumentCacheTargetEffectiveSettings _effectiveSettings =
        DocumentCacheTargetEffectiveSettings.FromOptions(CreateOptions([("", 1)]));

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef"
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

    [TestFixture]
    [Parallelizable]
    public class Given_Startup_Bound_Membership : DocumentCacheTargetRegistryTests
    {
        [Test]
        public void It_exposes_configured_targets_before_any_refresh()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7), ("", 1)]);

            DocumentCacheTargetRegistrySnapshot snapshot = fixture.Registry.CurrentSnapshot;

            snapshot
                .Targets.Select(target => target.TargetKey)
                .Should()
                .Equal(_tenantTargetKey, _defaultTargetKey);
            snapshot
                .Targets.Should()
                .AllSatisfy(target =>
                {
                    target.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Configured);
                    target.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.NotEvaluated);
                    target.EffectiveSettings.ProjectorFailureBackoff.Should().Be(TimeSpan.FromSeconds(10));
                });
        }

        [Test]
        public async Task It_does_not_expand_membership_from_runtime_options_mutation()
        {
            DocumentCacheOptions options = CreateOptions([("TenantA", 7)]);
            RegistryFixture fixture = new(options);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            options.Targets.Add(new DocumentCacheTargetOptions { TenantKey = "TenantB", DataStoreId = 8 });

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            snapshot.Targets.Select(target => target.TargetKey).Should().Equal(_tenantTargetKey);
            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Explicit_Refresh_Hooks : DocumentCacheTargetRegistryTests
    {
        [Test]
        public async Task It_calls_LoadDataStores_directly_for_a_configured_non_default_tenant()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");

            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.CmsRefreshNotification);

            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA");
            fixture.DataStoreProvider.LoadTenantsCallCount.Should().Be(0);
            fixture.DataStoreProvider.TenantExistsCallCount.Should().Be(0);
            fixture.DataStoreProvider.GetLoadedTenantKeysCallCount.Should().Be(0);
        }

        [Test]
        public async Task It_calls_LoadDataStores_once_per_configured_tenant()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7), ("TenantA", 9), ("", 1)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");
            fixture.DataStoreProvider.QueueLoadResult(null);

            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);

            fixture.DataStoreProvider.LoadDataStoreCalls.Should().Equal("TenantA", "");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Unresolved_Targets : DocumentCacheTargetRegistryTests
    {
        [Test]
        public async Task It_marks_configured_targets_missing_from_CMS_as_unresolved_with_retry_diagnostics()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Unresolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            observation.Generation.Should().BeNull();
            observation.RetryState!.AttemptCount.Should().Be(1);
            observation
                .RetryState.NextRetryAt.Should()
                .Be(fixture.TimeProvider.GetUtcNow() + TimeSpan.FromSeconds(10));
            observation
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.TargetUnresolved);
        }

        [Test]
        public async Task It_keeps_the_current_generation_when_CMS_refresh_fails_for_a_resolved_target()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadFailure("TenantA", new InvalidOperationException("boom"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.Generation!.Value.Should().Be(1);
            observation.RetryState!.AttemptCount.Should().Be(1);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure
                );
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_reports_unresolved_when_a_successful_refresh_no_longer_resolves_the_key()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult("TenantA");

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Unresolved);
            observation.Generation.Should().BeNull();
            observation
                .Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .NotContain(DocumentCacheTargetDiagnosticCategory.TargetReplaced);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Resolved_Targets : DocumentCacheTargetRegistryTests
    {
        [Test]
        public async Task It_resolves_a_loaded_target_with_the_first_context_generation()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.Startup
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            observation.Generation!.Value.Should().Be(1);
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
            fixture.ContextBuilder.BuildCalls[0].Generation.Value.Should().Be(1);
        }

        [Test]
        public async Task It_does_not_create_a_new_generation_for_display_or_route_context_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", name: "Original", routeContextValue: "2025")
            );
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", name: "Changed", routeContextValue: "2026")
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            snapshot.Targets.Single().Generation!.Value.Should().Be(1);
            fixture.ContextBuilder.BuildCalls.Should().ContainSingle();
        }

        [Test]
        public async Task It_creates_a_new_generation_when_the_connection_factory_input_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-b"));

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            DocumentCacheTargetObservation observation = snapshot.Targets.Single();
            observation.Generation!.Value.Should().Be(2);
            observation
                .Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.TargetReplaced
                );
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 2);
        }

        [Test]
        public async Task It_creates_a_new_generation_when_the_provider_token_changes()
        {
            RegistryFixture fixture = new(Targets: [("TenantA", 7)]);
            fixture.DataStoreProvider.QueueLoadResult("TenantA", CreateDataStore(7, "connection-a"));
            await fixture.Registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup);
            fixture.DataStoreProvider.QueueLoadResult(
                "TenantA",
                CreateDataStore(7, "connection-a", RelationalProviderToken.SqlServer)
            );

            DocumentCacheTargetRegistrySnapshot snapshot = await fixture.Registry.RefreshAsync(
                DocumentCacheTargetRefreshReason.CmsRefreshNotification
            );

            snapshot.Targets.Single().Generation!.Value.Should().Be(2);
            fixture.ContextBuilder.BuildCalls.Select(call => call.Generation.Value).Should().Equal(1, 2);
        }
    }

    private static DocumentCacheOptions CreateOptions(
        IReadOnlyList<(string TenantKey, long DataStoreId)> targets
    )
    {
        DocumentCacheOptions options = new()
        {
            Targets = targets
                .Select(target => new DocumentCacheTargetOptions
                {
                    TenantKey = target.TenantKey,
                    DataStoreId = target.DataStoreId,
                })
                .ToList(),
            Projector = new DocumentCacheProjectorOptions { FailureBackoff = TimeSpan.FromSeconds(10) },
        };

        return options;
    }

    private static DataStore CreateDataStore(
        long id,
        string connectionString,
        RelationalProviderToken? relationalProviderToken = null,
        string name = "Display name must not leak",
        string routeContextValue = "2025"
    ) =>
        new(
            id,
            "Operational",
            name,
            connectionString,
            new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("schoolYear")] = new(routeContextValue),
            },
            relationalProviderToken ?? RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported
        );

    private sealed class RegistryFixture
    {
        public SequencedDataStoreProvider DataStoreProvider { get; } = new();

        public RecordingTargetContextBuilder ContextBuilder { get; } = new();

        public FakeTimeProvider TimeProvider { get; } =
            new(new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

        public DocumentCacheTargetRegistry Registry { get; }

        public RegistryFixture(IReadOnlyList<(string TenantKey, long DataStoreId)> Targets)
            : this(CreateOptions(Targets)) { }

        public RegistryFixture(DocumentCacheOptions options)
        {
            Registry = new DocumentCacheTargetRegistry(
                DataStoreProvider,
                ContextBuilder,
                Options.Create(options),
                TimeProvider,
                NullLogger<DocumentCacheTargetRegistry>.Instance
            );
        }
    }

    private sealed class RecordingTargetContextBuilder : IDocumentCacheTargetContextBuilder
    {
        public List<BuildCall> BuildCalls { get; } = [];

        public Task<DocumentCacheTargetContextBuildResult> BuildAsync(
            DocumentCacheTargetKey targetKey,
            DocumentCacheTargetContextGeneration generation,
            CancellationToken cancellationToken = default
        )
        {
            BuildCalls.Add(new BuildCall(targetKey, generation));
            return Task.FromResult(CreateBuildResult(targetKey, generation));
        }

        private static DocumentCacheTargetContextBuildResult CreateBuildResult(
            DocumentCacheTargetKey targetKey,
            DocumentCacheTargetContextGeneration generation
        )
        {
            DocumentCacheTargetObservation observation = DocumentCacheTargetObservation.ResolvedEligible(
                targetKey,
                _effectiveSettings,
                generation,
                RelationalProviderToken.Postgresql,
                _fingerprint,
                _trackingLifecycle,
                _satisfiedInventory,
                _satisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );
            DocumentCacheTargetExecutionContext executionContext = new(
                targetKey,
                generation,
                _effectiveSettings,
                new DocumentCacheTargetDataStoreMetadata(targetKey.DataStoreId, "Operational"),
                new DocumentCacheTargetConnectionInput(RelationalProviderToken.Postgresql, "connection"),
                _fingerprint,
                _trackingLifecycle,
                _satisfiedInventory,
                _satisfiedEnqueueTrigger,
                DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
            );

            return new DocumentCacheTargetContextBuildResult(observation, executionContext);
        }
    }

    private sealed record BuildCall(
        DocumentCacheTargetKey TargetKey,
        DocumentCacheTargetContextGeneration Generation
    );

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
