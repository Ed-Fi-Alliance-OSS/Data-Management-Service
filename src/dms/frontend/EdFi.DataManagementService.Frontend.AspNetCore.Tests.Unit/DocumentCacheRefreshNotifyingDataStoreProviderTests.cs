// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class DocumentCacheRefreshNotifyingDataStoreProviderTests
{
    private static DataStore DataStore(
        long id = 1,
        string name = "Data Store",
        string connectionString = "Host=localhost;Database=edfi;",
        RelationalProviderToken? providerToken = null
    ) =>
        new(
            id,
            "Operational",
            name,
            connectionString,
            new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("schoolYear")] = new("2026"),
            },
            providerToken ?? RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported
        );

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Loaded_After_Application_Start
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingDataStoreProvider _dataStoreProvider = null!;
        private RecordingProjectionSupervisor _projectionSupervisor = null!;
        private IList<DataStore> _loadedDataStores = null!;

        [SetUp]
        public async Task Setup()
        {
            _dataStoreProvider = new RecordingDataStoreProvider([DataStore()]);
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(_dataStoreProvider, _projectionSupervisor, canNotify: true);

            _loadedDataStores = await provider.LoadDataStores("TenantA");
        }

        [Test]
        public void It_returns_the_loaded_data_stores()
        {
            _loadedDataStores.Should().ContainSingle().Which.Id.Should().Be(1);
        }

        [Test]
        public void It_notifies_the_projection_supervisor_with_the_cms_refresh_reason()
        {
            _projectionSupervisor
                .RefreshReasons.Should()
                .Equal(DocumentCacheTargetRefreshReason.CmsRefreshNotification);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Loaded_Before_Application_Start
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()]);
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: false);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_does_not_refresh_DocumentCache_targets_during_program_startup()
        {
            _projectionSupervisor.RefreshReasons.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Expired_DataStore_Metadata_Refresh_Replaces_A_Target
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore(connectionString: "first")])
            {
                OnRefreshInstancesIfExpired = provider =>
                    provider.CurrentDataStores = [DataStore(connectionString: "second")],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.RefreshInstancesIfExpiredAsync();
        }

        [Test]
        public void It_notifies_the_projection_supervisor_with_the_cms_refresh_reason()
        {
            _projectionSupervisor
                .RefreshReasons.Should()
                .Equal(DocumentCacheTargetRefreshReason.CmsRefreshNotification);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Expired_DataStore_Metadata_Refresh_Removes_A_Target
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()])
            {
                OnRefreshInstancesIfExpired = provider => provider.CurrentDataStores = [],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.RefreshInstancesIfExpiredAsync();
        }

        [Test]
        public void It_notifies_the_projection_supervisor_with_the_cms_refresh_reason()
        {
            _projectionSupervisor
                .RefreshReasons.Should()
                .Equal(DocumentCacheTargetRefreshReason.CmsRefreshNotification);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Refresh_Does_Not_Change_DataStore_Metadata
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()]);
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.RefreshInstancesIfExpiredAsync();
        }

        [Test]
        public void It_does_not_notify_the_projection_supervisor()
        {
            _projectionSupervisor.RefreshReasons.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Projection_Supervisor_Notification_Fails
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()]);
            _projectionSupervisor = new RecordingProjectionSupervisor
            {
                RefreshException = new InvalidOperationException("refresh failed"),
            };

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            _act = () => provider.LoadDataStores();
        }

        [Test]
        public async Task It_does_not_fail_the_data_store_load()
        {
            await _act.Should().NotThrowAsync();
            _projectionSupervisor.RefreshAttemptCount.Should().Be(1);
        }
    }

    private static DocumentCacheRefreshNotifyingDataStoreProvider CreateProvider(
        IDataStoreProvider dataStoreProvider,
        IDocumentCacheProjectionSupervisor projectionSupervisor,
        bool canNotify
    ) =>
        new(
            dataStoreProvider,
            projectionSupervisor,
            NullLogger<DocumentCacheRefreshNotifyingDataStoreProvider>.Instance,
            () => canNotify
        );

    private sealed class RecordingDataStoreProvider(IReadOnlyList<DataStore> currentDataStores)
        : IDataStoreProvider
    {
        public IReadOnlyList<DataStore> CurrentDataStores { get; set; } = currentDataStores;

        public Action<RecordingDataStoreProvider>? OnRefreshInstancesIfExpired { get; init; }

        public Task<IList<DataStore>> LoadDataStores(string? tenant = null) =>
            Task.FromResult<IList<DataStore>>(CurrentDataStores.ToList());

        public Task RefreshInstancesIfExpiredAsync(string? tenant = null)
        {
            OnRefreshInstancesIfExpired?.Invoke(this);
            return Task.CompletedTask;
        }

        public IReadOnlyList<DataStore> GetAll(string? tenant = null) =>
            CurrentDataStores.ToList().AsReadOnly();

        public DataStore? GetById(long id, string? tenant = null) =>
            CurrentDataStores.FirstOrDefault(dataStore => dataStore.Id == id);

        public bool IsLoaded(string? tenant = null) => true;

        public Task<IList<string>> LoadTenants() => Task.FromResult<IList<string>>([]);

        public bool TenantExists(string tenant) => true;

        public IReadOnlyList<string> GetLoadedTenantKeys() => [""];
    }

    private sealed class RecordingProjectionSupervisor : IDocumentCacheProjectionSupervisor
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts => [];

        public List<DocumentCacheTargetRefreshReason> RefreshReasons { get; } = [];

        public int RefreshAttemptCount { get; private set; }

        public Exception? RefreshException { get; init; }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            RefreshAttemptCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (RefreshException is not null)
            {
                throw RefreshException;
            }

            RefreshReasons.Add(reason);
            return Task.FromResult(new DocumentCacheTargetRegistrySnapshot([], DateTimeOffset.UtcNow));
        }
    }
}
