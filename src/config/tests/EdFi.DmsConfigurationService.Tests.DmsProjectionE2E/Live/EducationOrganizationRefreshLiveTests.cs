// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using FluentAssertions;
using Codes = EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh.EducationOrganizationRefreshJobErrorCodes;
using ProjectionCodes = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionJobErrorCodes;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>
/// Refresh jobs run by a real Configuration Service job worker against the live DMS (<see cref="RefreshHost"/>), each
/// case on its own <see cref="ScratchCmsDatabase"/>: pagination, a permanent failure on one of two data stores, a page
/// timeout, an unreachable DMS, a data store DMS does not have, and a retry that succeeds. Every case reads the
/// job's terminal status as <c>GET /v3/jobs/{jobId}</c> serves it, and the data stores' active snapshots.
/// </summary>
public static class EducationOrganizationRefreshLiveTests
{
    private const string Tenant = ProjectionE2EFixture.SeededTenant;

    /// <summary>A snapshot planted before a refresh, which a refresh that publishes replaces.</summary>
    private static readonly IReadOnlyList<SnapshotRow> _planted =
    [
        new(999999901, "Planted before the refresh", null, "edfi.School", null),
    ];

    private static ProjectionE2EFixture Fixture => ProjectionE2EFixture.Current;

    private static readonly SemaphoreSlim _sharedGate = new(1, 1);
    private static readonly Dictionary<int, IReadOnlyList<SnapshotRow>> _served = [];
    private static IReadOnlyDictionary<string, ClientCredentials>? _clients;

    /// <summary>The seeded data store, <c>255901/2025</c>.</summary>
    private static int SeededStore =>
        Fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier).DataStoreId;

    /// <summary>The seeded tenant's other data store, <c>255901/2024</c>.</summary>
    private static int SiblingStore => Fixture.Environment.Route("255901/2024").DataStoreId;

    private static IReadOnlyList<SnapshotRow> SeededRows => [.. ProjectionE2EFixture.SeededItems.Select(Row)];

    private static SnapshotRow Row(EducationOrganizationProjectionItem item) =>
        new(
            item.EducationOrganizationId,
            item.NameOfInstitution,
            item.ShortNameOfInstitution,
            item.Discriminator,
            item.ParentId
        );

    private static string MessageOf(string code) =>
        ProjectionCodes.All.Concat(Codes.All).Single(pair => pair.Key == code).Value;

    /// <summary>The public text of a refresh of every data store that failed with <paramref name="categories"/>.</summary>
    private static string IncompleteMessage(params string[] categories) =>
        $"{Codes.IncompleteMessage} Problem categories: {string.Join(", ", categories)}. "
        + string.Join(" ", categories.Select(MessageOf));

    /// <summary>
    /// One case: a fresh scratch database, an optional arrangement, a host with the case's settings, then the case's
    /// refreshes. The host is stopped when the fixture ends; the database is dropped when the run ends.
    /// </summary>
    public abstract class LiveRefreshCase
    {
        protected ScratchCmsDatabase Database { get; private set; } = null!;

        protected RefreshHost Host { get; private set; } = null!;

        protected virtual RefreshHostSettings Settings => new();

        [OneTimeSetUp]
        public async Task RunCaseAsync()
        {
            Database = await ScratchCmsDatabase.CreateAsync(Fixture);
            await ArrangeAsync();
            Host = await RefreshHost.StartAsync(Fixture, Database, Settings, await ClientsAsync());
            await ActAsync();
        }

        [OneTimeTearDown]
        public async Task StopHostAsync()
        {
            if (Host is not null)
            {
                await Host.DisposeAsync();
            }
        }

        protected virtual Task ArrangeAsync() => Task.CompletedTask;

        protected abstract Task ActAsync();

        /// <summary>
        /// The cases' own projection credential for <see cref="Tenant"/>, created once per run. The Configuration
        /// Service caps a client's active tokens, and every reader host takes a token, so the cases do not share the
        /// reader fixtures' credential. <see cref="LiveServices"/> registers the claim set and application for cleanup.
        /// </summary>
        protected static async Task<IReadOnlyDictionary<string, ClientCredentials>> ClientsAsync()
        {
            await _sharedGate.WaitAsync();
            try
            {
                if (_clients is null)
                {
                    string claimSetName = $"LiveRefreshE2E{Tenant[^6..]}";
                    await Fixture.Services.ImportProjectionClaimSetAsync(Tenant, claimSetName);
                    ClientCredentials client = await Fixture.Services.CreateApplicationAsync(
                        Tenant,
                        $"Live Refresh E2E {Tenant}",
                        claimSetName,
                        dataStoreIds: []
                    );
                    await Fixture.Services.ReloadClaimSetsAsync(Tenant);
                    _clients = new Dictionary<string, ClientCredentials>(StringComparer.Ordinal)
                    {
                        [Tenant] = client,
                    };
                }

                return _clients;
            }
            finally
            {
                _sharedGate.Release();
            }
        }

        /// <summary>A refresh of the seeded data store that must complete, as case 1 publishes it.</summary>
        protected async Task<ActiveSnapshot> PublishSeededStoreAsync()
        {
            JobStatusResponse status = await Host.RunAsync(
                Tenant,
                EducationOrganizationRefreshScope.One,
                SeededStore
            );
            ActiveSnapshot? snapshot = await Database.ActiveSnapshotAsync(SeededStore);
            if (status.Status != "Completed" || snapshot?.Rows.SequenceEqual(SeededRows) != true)
            {
                throw new InvalidOperationException(
                    $"Setup failed: refreshing the seeded data store ended {status.Status} ({status.ErrorMessage}). "
                        + $"Host warnings: {string.Join(" | ", Host.Warnings)}"
                );
            }

            return snapshot;
        }

        /// <summary>
        /// What DMS serves for the data store, read once per run by the production reader outside any job. The cases
        /// never write to DMS, so one read stands for all of them.
        /// </summary>
        protected static async Task<IReadOnlyList<SnapshotRow>> ServedAsync(int dataStoreId)
        {
            IReadOnlyDictionary<string, ClientCredentials> clients = await ClientsAsync();
            await _sharedGate.WaitAsync();
            try
            {
                if (!_served.TryGetValue(dataStoreId, out IReadOnlyList<SnapshotRow>? served))
                {
                    served = await ReadServedAsync(dataStoreId, clients);
                    _served[dataStoreId] = served;
                }

                return served;
            }
            finally
            {
                _sharedGate.Release();
            }
        }

        private static async Task<IReadOnlyList<SnapshotRow>> ReadServedAsync(
            int dataStoreId,
            IReadOnlyDictionary<string, ClientCredentials> clients
        )
        {
            using ReaderHost reader = new(Fixture, clients: clients);
            EducationOrganizationProjectionReadResult result = await reader.ReadAsync(
                Tenant,
                dataStoreId,
                await Fixture.Services.DataStoreContextsAsync(Tenant, dataStoreId)
            );
            return result switch
            {
                EducationOrganizationProjectionReadResult.Success success => [.. success.Items.Select(Row)],
                EducationOrganizationProjectionReadResult.Failure { Detail: var failure } =>
                    throw new InvalidOperationException(
                        $"Setup failed: reading data store {dataStoreId} failed: {failure.Category} {failure.Code} "
                            + $"at {failure.Stage}, HTTP {failure.HttpStatus}, {failure.ProblemType}."
                    ),
                _ => throw new InvalidOperationException(
                    $"Setup failed: reading data store {dataStoreId} failed."
                ),
            };
        }
    }

    /// <summary>Case 1: two items a page, so the seeded store takes four pages.</summary>
    [TestFixture]
    public class Given_a_refresh_of_every_data_store_of_the_seeded_tenant : LiveRefreshCase
    {
        private JobStatusResponse _status = null!;
        private ActiveSnapshot? _seeded;
        private ActiveSnapshot? _sibling;
        private IReadOnlyList<SnapshotRow> _siblingServed = [];

        protected override async Task ActAsync()
        {
            _status = await Host.RunAsync(Tenant, EducationOrganizationRefreshScope.All);
            _seeded = await Database.ActiveSnapshotAsync(SeededStore);
            _sibling = await Database.ActiveSnapshotAsync(SiblingStore);
            _siblingServed = await ServedAsync(SiblingStore);
        }

        [Test]
        public void It_completes() =>
            _status.Should().BeEquivalentTo(new { Status = "Completed", ErrorMessage = (string?)null });

        [Test]
        public void It_publishes_exactly_the_seeded_hierarchy() => _seeded!.Rows.Should().Equal(SeededRows);

        [Test]
        public void It_reads_the_seeded_data_store_in_one_walk_of_four_pages() =>
            Host
                .Plan.Pages.Where(page => page.DataStoreId == SeededStore)
                .Select(page => page.AttemptPage)
                .Should()
                .Equal(1, 2, 3, 4);

        [Test]
        public void It_publishes_the_other_data_store_as_dms_serves_it() =>
            _sibling!.Rows.Should().Equal(_siblingServed);
    }

    /// <summary>Case 2: a permanent failure on page 3 of the seeded store, while the other store refreshes.</summary>
    [TestFixture]
    public class Given_a_permanent_failure_on_one_of_two_data_stores : LiveRefreshCase
    {
        private ActiveSnapshot _seededBefore = null!;
        private JobStatusResponse _status = null!;
        private ActiveSnapshot? _seeded;
        private ActiveSnapshot? _sibling;
        private IReadOnlyList<SnapshotRow> _siblingServed = [];

        protected override Task ArrangeAsync() => Database.PlantSnapshotAsync(Tenant, SiblingStore, _planted);

        protected override async Task ActAsync()
        {
            _seededBefore = await PublishSeededStoreAsync();
            Host.Plan.OnPage = (page, _) =>
                Task.FromResult(
                    page.DataStoreId == SeededStore && page.AttemptPage == 3
                        ? ReaderHost.ContractProblem("target-schema-incompatible")
                        : null
                );

            _status = await Host.RunAsync(Tenant, EducationOrganizationRefreshScope.All);
            _seeded = await Database.ActiveSnapshotAsync(SeededStore);
            _sibling = await Database.ActiveSnapshotAsync(SiblingStore);
            _siblingServed = await ServedAsync(SiblingStore);
        }

        [Test]
        public void It_fails_with_the_composite_listing_the_schema_category() =>
            _status
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = "Error",
                        ErrorMessage = IncompleteMessage(ProjectionCodes.TargetSchemaIncompatible),
                    }
                );

        [Test]
        public void It_replaces_the_planted_snapshot_of_the_data_store_that_refreshed()
        {
            _sibling!.Generation.Should().BeGreaterThan(1);
            _sibling.Rows.Should().Equal(_siblingServed);
        }

        [Test]
        public void It_keeps_the_previous_snapshot_of_the_data_store_that_failed() =>
            _seeded.Should().BeEquivalentTo(_seededBefore);
    }

    /// <summary>Case 3: page 2 of the seeded store stalls past a one-second page timeout, with one attempt.</summary>
    [TestFixture]
    public class Given_a_page_that_outlasts_its_timeout : LiveRefreshCase
    {
        private ActiveSnapshot _before = null!;
        private JobStatusResponse _status = null!;
        private ActiveSnapshot? _after;

        protected override RefreshHostSettings Settings => new() { PageRequestTimeoutSeconds = 1 };

        protected override async Task ActAsync()
        {
            _before = await PublishSeededStoreAsync();
            Host.Plan.OnPage = async (page, cancellationToken) =>
            {
                if (page.DataStoreId == SeededStore && page.AttemptPage == 2)
                {
                    // Ends only when the reader's page timeout cancels the request.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return null;
            };

            _status = await Host.RunAsync(Tenant, EducationOrganizationRefreshScope.One, SeededStore);
            _after = await Database.ActiveSnapshotAsync(SeededStore);
        }

        [Test]
        public void It_fails_as_temporarily_unavailable() =>
            _status
                .Should()
                .BeEquivalentTo(
                    new { Status = "Error", ErrorMessage = MessageOf(Codes.TemporarilyUnavailable) }
                );

        [Test]
        public void It_keeps_the_previous_snapshot() => _after.Should().BeEquivalentTo(_before);
    }

    /// <summary>Case 4: DMS is an unused local port, with one attempt.</summary>
    [TestFixture]
    public class Given_dms_is_unreachable : LiveRefreshCase
    {
        private JobStatusResponse _status = null!;
        private ActiveSnapshot? _seeded;
        private ActiveSnapshot? _sibling;

        protected override RefreshHostSettings Settings => new() { DmsBaseUrl = UnusedLocalPort() };

        protected override async Task ArrangeAsync()
        {
            await Database.PlantSnapshotAsync(Tenant, SiblingStore, _planted);
            await Database.PlantSnapshotAsync(Tenant, SeededStore, SeededRows);
        }

        protected override async Task ActAsync()
        {
            _status = await Host.RunAsync(Tenant, EducationOrganizationRefreshScope.All);
            _seeded = await Database.ActiveSnapshotAsync(SeededStore);
            _sibling = await Database.ActiveSnapshotAsync(SiblingStore);
        }

        [Test]
        public void It_fails_with_the_composite_listing_temporary_unavailability() =>
            _status
                .Should()
                .BeEquivalentTo(
                    new { Status = "Error", ErrorMessage = IncompleteMessage(Codes.TemporarilyUnavailable) }
                );

        [Test]
        public void It_keeps_every_previous_snapshot()
        {
            _seeded.Should().BeEquivalentTo(new ActiveSnapshot(1, SeededRows));
            _sibling.Should().BeEquivalentTo(new ActiveSnapshot(1, _planted));
        }

        [Test]
        public void It_requests_no_page() => Host.Plan.Pages.Should().BeEmpty();

        private static Uri UnusedLocalPort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return new Uri($"http://127.0.0.1:{port}/");
        }
    }

    /// <summary>
    /// Case 5: a data store this Configuration Service holds, with the seeded store's contexts, under an identifier the
    /// deployed Configuration Service, and so DMS, does not have.
    /// </summary>
    [TestFixture]
    public class Given_a_data_store_dms_does_not_have : LiveRefreshCase
    {
        private int _unknownStore;
        private JobStatusResponse _status = null!;
        private ActiveSnapshot? _snapshot;

        protected override async Task ArrangeAsync()
        {
            _unknownStore = Fixture.Environment.Routes.Max(route => route.DataStoreId) + 1000;
            await Database.InsertDataStoreAsync(
                Tenant,
                _unknownStore,
                await Fixture.Services.DataStoreContextsAsync(Tenant, SeededStore)
            );
        }

        protected override async Task ActAsync()
        {
            _status = await Host.RunAsync(Tenant, EducationOrganizationRefreshScope.One, _unknownStore);
            _snapshot = await Database.ActiveSnapshotAsync(_unknownStore);
        }

        [Test]
        public void It_fails_with_the_target_not_found_message() =>
            _status
                .Should()
                .BeEquivalentTo(
                    new { Status = "Error", ErrorMessage = MessageOf(ProjectionCodes.TargetNotFound) }
                );

        [Test]
        public void It_publishes_nothing_for_the_data_store() => _snapshot.Should().BeNull();
    }

    /// <summary>Case 6: a transient failure on the first attempt only, with two attempts one second apart.</summary>
    [TestFixture]
    public class Given_a_transient_failure_on_the_first_attempt_only : LiveRefreshCase
    {
        private int _failed;
        private JobStatusResponse _status = null!;
        private ActiveSnapshot? _seeded;
        private ActiveSnapshot? _sibling;
        private IReadOnlyList<SnapshotRow> _siblingServed = [];

        protected override RefreshHostSettings Settings =>
            new() { MaxAttempts = 2, RetryBackoffBase = TimeSpan.FromSeconds(1) };

        protected override async Task ActAsync()
        {
            Host.Plan.OnPage = (page, _) =>
                Task.FromResult(
                    page.DataStoreId == SeededStore && Interlocked.Exchange(ref _failed, 1) == 0
                        ? ReaderHost.ContractProblem("target-unavailable")
                        : null
                );

            _status = await Host.RunAsync(Tenant, EducationOrganizationRefreshScope.All);
            _seeded = await Database.ActiveSnapshotAsync(SeededStore);
            _sibling = await Database.ActiveSnapshotAsync(SiblingStore);
            _siblingServed = await ServedAsync(SiblingStore);
        }

        [Test]
        public void It_completes() =>
            _status.Should().BeEquivalentTo(new { Status = "Completed", ErrorMessage = (string?)null });

        [Test]
        public void It_reads_every_data_store_again_on_the_second_attempt()
        {
            int[] stores = [.. new[] { SiblingStore, SeededStore }.Order()];
            Host.Plan.Pages.Where(page => page.AttemptPage == 1)
                .Select(page => page.DataStoreId)
                .Should()
                .Equal(stores[0], stores[1], stores[0], stores[1]);
        }

        [Test]
        public void It_publishes_both_data_stores()
        {
            _seeded!.Rows.Should().Equal(SeededRows);
            _sibling!.Rows.Should().Equal(_siblingServed);
        }
    }
}
