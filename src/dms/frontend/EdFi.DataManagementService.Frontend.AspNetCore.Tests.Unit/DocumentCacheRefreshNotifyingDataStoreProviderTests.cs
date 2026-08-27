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
using Microsoft.Extensions.Logging;
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
        RelationalProviderToken? providerToken = null,
        IEnumerable<KeyValuePair<DataStoreDerivativeType, string>>? derivatives = null
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
            RelationalProviderMetadataStatus.Supported,
            derivatives
        );

    private static IEnumerable<KeyValuePair<DataStoreDerivativeType, string>> Derivatives(
        string? snapshot = null,
        string? readReplica = null
    )
    {
        if (snapshot is not null)
        {
            yield return new KeyValuePair<DataStoreDerivativeType, string>(
                DataStoreDerivativeType.Snapshot,
                snapshot
            );
        }

        if (readReplica is not null)
        {
            yield return new KeyValuePair<DataStoreDerivativeType, string>(
                DataStoreDerivativeType.ReadReplica,
                readReplica
            );
        }
    }

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
            _dataStoreProvider = new RecordingDataStoreProvider([])
            {
                OnLoadDataStores = provider => provider.CurrentDataStores = [DataStore()],
            };
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
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
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
            var dataStoreProvider = new RecordingDataStoreProvider([])
            {
                OnLoadDataStores = provider => provider.CurrentDataStores = [DataStore()],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: false);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_does_not_refresh_DocumentCache_targets_during_program_startup()
        {
            _projectionSupervisor.SignalCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Does_Not_Change_Targets
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;
        private IList<DataStore> _loadedDataStores = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()]);
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            _loadedDataStores = await provider.LoadDataStores("TenantA");
        }

        [Test]
        public void It_returns_the_loaded_data_stores()
        {
            _loadedDataStores.Should().ContainSingle().Which.Id.Should().Be(1);
        }

        [Test]
        public void It_does_not_notify_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Replaces_A_Target
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore(connectionString: "first")])
            {
                OnLoadDataStores = provider =>
                    provider.CurrentDataStores = [DataStore(connectionString: "second")],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Removes_A_Target
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()])
            {
                OnLoadDataStores = provider => provider.CurrentDataStores = [],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
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
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Expired_DataStore_Metadata_Changes_While_Supervisor_Reconciliation_Would_Block
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;
        private TaskCompletionSource<DocumentCacheTargetRegistrySnapshot> _reconciliationCompletion = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore(connectionString: "first")])
            {
                OnRefreshInstancesIfExpired = provider =>
                    provider.CurrentDataStores = [DataStore(connectionString: "second")],
            };
            _reconciliationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _projectionSupervisor = new RecordingProjectionSupervisor
            {
                RefreshCompletion = _reconciliationCompletion,
            };
            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.RefreshInstancesIfExpiredAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }

        [Test]
        public void It_returns_after_signaling_without_awaiting_supervisor_reconciliation()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
            _projectionSupervisor.RefreshAttemptCount.Should().Be(0);
            _reconciliationCompletion.Task.IsCompleted.Should().BeFalse();
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
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
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
            _projectionSupervisor.SignalCount.Should().Be(0);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Load_Finds_No_DataStore_Targets : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;
        private IList<DataStore> _loadedDataStores = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([]);
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            _loadedDataStores = await provider.LoadDataStores();
        }

        [Test]
        public void It_returns_no_data_stores()
        {
            _loadedDataStores.Should().BeEmpty();
        }

        [Test]
        public void It_does_not_notify_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(0);
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
            var dataStoreProvider = new RecordingDataStoreProvider([])
            {
                OnLoadDataStores = provider => provider.CurrentDataStores = [DataStore()],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor
            {
                SignalException = new InvalidOperationException("signal failed"),
            };

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            _act = () => provider.LoadDataStores();
        }

        [Test]
        public async Task It_does_not_fail_the_data_store_load()
        {
            await _act.Should().NotThrowAsync();
            _projectionSupervisor.SignalAttemptCount.Should().Be(1);
        }
    }

    /// <summary>
    /// A change confined to a derivative is a data store metadata change like any other. Leaving
    /// derivatives out of the comparison would let a refresh that only added, replaced, or removed one
    /// pass unnoticed, so each of those three shapes must signal.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Adds_A_Derivative
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([DataStore()])
            {
                OnLoadDataStores = provider =>
                    provider.CurrentDataStores = [
                        DataStore(derivatives: Derivatives(snapshot: "Host=snapshot;Database=edfi;")),
                    ],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Replaces_A_Derivative_Connection_String
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([
                DataStore(derivatives: Derivatives(snapshot: "Host=first-snapshot;Database=edfi;")),
            ])
            {
                OnLoadDataStores = provider =>
                    provider.CurrentDataStores = [
                        DataStore(derivatives: Derivatives(snapshot: "Host=second-snapshot;Database=edfi;")),
                    ],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Removes_A_Derivative
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private RecordingProjectionSupervisor _projectionSupervisor = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([
                DataStore(
                    derivatives: Derivatives(
                        snapshot: "Host=snapshot;Database=edfi;",
                        readReplica: "Host=replica;Database=edfi;"
                    )
                ),
            ])
            {
                OnLoadDataStores = provider =>
                    provider.CurrentDataStores = [
                        DataStore(derivatives: Derivatives(snapshot: "Host=snapshot;Database=edfi;")),
                    ],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_signals_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(1);
        }
    }

    /// <summary>
    /// The comparison is by content, not by map identity, so a reload that produces an equal derivative
    /// map is not a change. It reads only already-loaded configuration and emits no log of its own,
    /// which is what keeps connection strings out of the log on this path.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_DataStore_Metadata_Load_Leaves_Derivatives_Unchanged
        : DocumentCacheRefreshNotifyingDataStoreProviderTests
    {
        private const string SnapshotConnectionString = "Host=snapshot;Database=edfi;";

        private RecordingProjectionSupervisor _projectionSupervisor = null!;
        private CapturingLogger<DocumentCacheRefreshNotifyingDataStoreProvider> _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            var dataStoreProvider = new RecordingDataStoreProvider([
                DataStore(derivatives: Derivatives(snapshot: SnapshotConnectionString)),
            ])
            {
                OnLoadDataStores = provider =>
                    provider.CurrentDataStores = [
                        DataStore(derivatives: Derivatives(snapshot: SnapshotConnectionString)),
                    ],
            };
            _projectionSupervisor = new RecordingProjectionSupervisor();
            _logger = new CapturingLogger<DocumentCacheRefreshNotifyingDataStoreProvider>();

            var provider = CreateProvider(dataStoreProvider, _projectionSupervisor, canNotify: true, _logger);

            await provider.LoadDataStores();
        }

        [Test]
        public void It_does_not_notify_the_projection_supervisor()
        {
            _projectionSupervisor.SignalCount.Should().Be(0);
        }

        [Test]
        public void It_logs_nothing_while_comparing_metadata()
        {
            _logger.Messages.Should().BeEmpty();
        }
    }

    private static DocumentCacheRefreshNotifyingDataStoreProvider CreateProvider(
        IDataStoreProvider dataStoreProvider,
        IDocumentCacheProjectionRefreshSignal projectionRefreshSignal,
        bool canNotify,
        ILogger<DocumentCacheRefreshNotifyingDataStoreProvider>? logger = null
    ) =>
        new(
            dataStoreProvider,
            projectionRefreshSignal,
            logger ?? NullLogger<DocumentCacheRefreshNotifyingDataStoreProvider>.Instance,
            () => canNotify
        );

    /// <summary>
    /// Captures every log line so a test can prove the metadata comparison emits none at all, which is
    /// what keeps connection material out of the log on that path.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => [.. _messages];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _messages.Add(formatter(state, exception));
        }
    }

    private sealed class RecordingDataStoreProvider(IReadOnlyList<DataStore> currentDataStores)
        : IDataStoreProvider
    {
        public IReadOnlyList<DataStore> CurrentDataStores { get; set; } = currentDataStores;

        public Action<RecordingDataStoreProvider>? OnLoadDataStores { get; init; }

        public Action<RecordingDataStoreProvider>? OnRefreshInstancesIfExpired { get; init; }

        public Task<IList<DataStore>> LoadDataStores(
            string? tenant = null,
            CancellationToken cancellationToken = default
        )
        {
            OnLoadDataStores?.Invoke(this);
            return Task.FromResult<IList<DataStore>>(CurrentDataStores.ToList());
        }

        public Task RefreshInstancesIfExpiredAsync(
            string? tenant = null,
            CancellationToken cancellationToken = default
        )
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

    private sealed class RecordingProjectionSupervisor
        : IDocumentCacheProjectionSupervisor,
            IDocumentCacheProjectionRefreshSignal
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts => [];

        public int SignalCount { get; private set; }

        public int RefreshAttemptCount { get; private set; }

        public int SignalAttemptCount { get; private set; }

        public Exception? SignalException { get; init; }

        public TaskCompletionSource<DocumentCacheTargetRegistrySnapshot>? RefreshCompletion { get; init; }

        public void SignalRefresh()
        {
            SignalAttemptCount++;

            if (SignalException is not null)
            {
                throw SignalException;
            }

            SignalCount++;
        }

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            RefreshAttemptCount++;
            cancellationToken.ThrowIfCancellationRequested();

            return RefreshCompletion?.Task
                ?? Task.FromResult(new DocumentCacheTargetRegistrySnapshot([], DateTimeOffset.UtcNow));
        }
    }
}
