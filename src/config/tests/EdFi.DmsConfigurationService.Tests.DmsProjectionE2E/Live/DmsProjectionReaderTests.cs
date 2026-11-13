// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;
using FailureCategory = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCategory;
using FailureCode = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using FailureStage = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionStage;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>
/// The production reader against the live DMS of the Instance Management stack (DMS-1440 spec §9 step 4.2): real
/// Discovery, real tokens, real pages, with faults injected only where a case names one.
/// </summary>
public static class DmsProjectionReaderTests
{
    private const string TargetNotFoundType =
        "urn:ed-fi:api:education-organization-projection:target-not-found";
    private const string TargetUnavailableType =
        "urn:ed-fi:api:education-organization-projection:target-unavailable";
    private const string DatabaseNotProvisionedType = "urn:ed-fi:api:database-not-provisioned";

    /// <summary>The route 2 connection string, naming <paramref name="database"/> on the same server instead.</summary>
    private static string RouteTwoServerDatabase(string database)
    {
        DbConnectionStringBuilder connection = new()
        {
            ConnectionString = ProjectionE2EFixture.Current.Environment.RouteTwoConnectionString,
        };
        string databaseKey =
            connection
                .Keys.Cast<string>()
                .SingleOrDefault(key =>
                    key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)
                )
            ?? throw new InvalidOperationException(
                "Setup failed: the route 2 connection string names no database."
            );
        connection[databaseKey] = database;
        return connection.ConnectionString;
    }

    /// <summary>A read of one fixture route through a fresh production registration.</summary>
    public abstract class ReadFixture
    {
        protected ReaderHost Host { get; private set; } = null!;

        protected EducationOrganizationProjectionReadResult Result { get; private set; } = null!;

        protected static ProjectionE2EFixture Fixture => ProjectionE2EFixture.Current;

        protected EducationOrganizationProjectionReadResult.Success Success =>
            Result.Should().BeOfType<EducationOrganizationProjectionReadResult.Success>().Subject;

        protected EducationOrganizationProjectionFailure Failure =>
            Result.Should().BeOfType<EducationOrganizationProjectionReadResult.Failure>().Subject.Detail;

        [OneTimeSetUp]
        public async Task ReadAsync()
        {
            await PrepareAsync();
            Host = new ReaderHost(Fixture);
            Arrange(Host.Plan);
            (string tenant, int dataStoreId) = Target();
            Result = await Host.ReadAsync(
                tenant,
                dataStoreId,
                await Fixture.Services.DataStoreContextsAsync(tenant, ContextsOf(dataStoreId))
            );
        }

        [OneTimeTearDown]
        public void DisposeHost() => Host?.Dispose();

        /// <summary>Creates what the case reads, before the read; NUnit runs a base fixture's set-up first.</summary>
        protected virtual Task PrepareAsync() => Task.CompletedTask;

        protected virtual (string Tenant, int DataStoreId) Target() =>
            (
                ProjectionE2EFixture.SeededTenant,
                Fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier).DataStoreId
            );

        /// <summary>The data store whose catalog contexts the request carries; the target itself by default.</summary>
        protected virtual int ContextsOf(int dataStoreId) => dataStoreId;

        protected virtual void Arrange(FaultPlan plan) { }
    }

    [TestFixture]
    public class Given_the_seeded_store_read_two_items_a_page : ReadFixture
    {
        [Test]
        public void It_returns_every_item_with_its_parent_in_id_order() =>
            Success.Items.Should().Equal(ProjectionE2EFixture.SeededItems);

        [Test]
        public void It_reads_four_pages_without_a_restart()
        {
            Success.PageCount.Should().Be(4);
            Success.Restarts.Should().Be(0);
        }

        [Test]
        public void It_reads_the_supported_contract_version() =>
            Success.ContractVersion.Should().Be("educationOrganizationProjection.v1");

        [Test]
        public void It_reads_discovery_once_then_one_token_then_one_walk() =>
            Host
                .Plan.Requests.Should()
                .Equal(
                    new RecordedRequest(RequestKind.Discovery, 0, 0),
                    new RecordedRequest(RequestKind.Token, 0, 0),
                    new RecordedRequest(RequestKind.Page, 1, 1),
                    new RecordedRequest(RequestKind.Page, 1, 2),
                    new RecordedRequest(RequestKind.Page, 1, 3),
                    new RecordedRequest(RequestKind.Page, 1, 4)
                );
    }

    [TestFixture]
    public class Given_a_provisioned_store_with_no_education_organization : ReadFixture
    {
        protected override (string Tenant, int DataStoreId) Target() =>
            (
                ProjectionE2EFixture.EmptyTenant,
                Fixture.Environment.Route(ProjectionE2EFixture.EmptyQualifier).DataStoreId
            );

        [Test]
        public void It_succeeds_with_no_items() => Success.Items.Should().BeEmpty();

        [Test]
        public void It_reads_one_page() => Success.PageCount.Should().Be(1);
    }

    [TestFixture]
    public class Given_a_data_store_id_the_tenant_does_not_have : ReadFixture
    {
        protected override (string Tenant, int DataStoreId) Target() =>
            (ProjectionE2EFixture.SeededTenant, int.MaxValue);

        // The request carries a real route of the tenant, so only the id is wrong.
        protected override int ContextsOf(int dataStoreId) =>
            Fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier).DataStoreId;

        [Test]
        public void It_fails_as_target_not_found() =>
            Failure
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Category = FailureCategory.Permanent,
                        Code = FailureCode.TargetNotFound,
                        Stage = FailureStage.Page,
                        HttpStatus = 404,
                        ProblemType = TargetNotFoundType,
                        PagesRead = 0,
                    }
                );
    }

    /// <summary>
    /// A store registered after DMS started, so DMS finds it by reloading its catalog on the miss, whose connection
    /// string names a database that does not exist.
    /// </summary>
    [TestFixture]
    public class Given_a_store_whose_database_does_not_exist : ReadFixture
    {
        private int _dataStoreId;

        protected override async Task PrepareAsync()
        {
            _dataStoreId = await Fixture.Services.CreateDataStoreAsync(
                ProjectionE2EFixture.SeededTenant,
                "Projection Reader E2E missing database",
                RouteTwoServerDatabase("edfi_dms_projection_reader_e2e_missing"),
                new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2099" }
            );
        }

        protected override (string Tenant, int DataStoreId) Target() =>
            (ProjectionE2EFixture.SeededTenant, _dataStoreId);

        [Test]
        public void It_fails_as_a_transient_target_unavailable_page() =>
            Failure
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Category = FailureCategory.Transient,
                        Code = FailureCode.ServiceUnavailable,
                        Stage = FailureStage.Page,
                        HttpStatus = 503,
                        ProblemType = TargetUnavailableType,
                        PagesRead = 0,
                    }
                );
    }

    /// <summary>
    /// A store registered against a reachable database that was never provisioned: the server's own system
    /// database (<c>postgres</c> or <c>master</c>), reached with the route databases' credentials, which holds no
    /// effective-schema table. DMS's only read of it is the fingerprint query, which finds none.
    /// </summary>
    [TestFixture]
    public class Given_a_store_whose_database_was_never_provisioned : ReadFixture
    {
        private int _dataStoreId;

        protected override async Task PrepareAsync()
        {
            _dataStoreId = await Fixture.Services.CreateDataStoreAsync(
                ProjectionE2EFixture.SeededTenant,
                "Projection Reader E2E never provisioned",
                RouteTwoServerDatabase(Fixture.Environment.DatabaseEngine == "mssql" ? "master" : "postgres"),
                new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2097" }
            );
        }

        protected override (string Tenant, int DataStoreId) Target() =>
            (ProjectionE2EFixture.SeededTenant, _dataStoreId);

        // A failure result carries no items: the type of the result is what exposes none.
        [Test]
        public void It_fails_with_no_items_as_a_transient_database_not_provisioned_page() =>
            Failure
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Category = FailureCategory.Transient,
                        Code = FailureCode.ServiceUnavailable,
                        Stage = FailureStage.Page,
                        HttpStatus = 503,
                        ProblemType = DatabaseNotProvisionedType,
                        PagesRead = 0,
                    }
                );
    }

    [TestFixture]
    public class Given_a_store_registered_without_a_school_year_context : ReadFixture
    {
        private int _dataStoreId;

        protected override async Task PrepareAsync()
        {
            _dataStoreId = await Fixture.Services.CreateDataStoreAsync(
                ProjectionE2EFixture.SeededTenant,
                "Projection Reader E2E no school year",
                Fixture.Environment.RouteTwoConnectionString,
                new Dictionary<string, string> { ["districtId"] = "255901" }
            );
        }

        protected override (string Tenant, int DataStoreId) Target() =>
            (ProjectionE2EFixture.SeededTenant, _dataStoreId);

        [Test]
        public void It_fails_as_target_not_routable()
        {
            Failure.Code.Should().Be(FailureCode.TargetNotRoutable);
            Failure.Category.Should().Be(FailureCategory.Permanent);
        }

        [Test]
        public void It_requests_no_page() => Host.Plan.Pages.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_dms_answers_503_on_page_3 : ReadFixture
    {
        protected override void Arrange(FaultPlan plan) =>
            plan.OnPage = (page, _) =>
                Task.FromResult(
                    page.AttemptPage == 3 ? ReaderHost.ContractProblem("target-unavailable") : null
                );

        [Test]
        public void It_fails_as_transient_with_no_items()
        {
            Result.Should().BeOfType<EducationOrganizationProjectionReadResult.Failure>();
            Failure.Category.Should().Be(FailureCategory.Transient);
            Failure.Code.Should().Be(FailureCode.ServiceUnavailable);
        }

        [Test]
        public void It_reports_the_two_pages_read_before_the_failure()
        {
            Failure.Stage.Should().Be(FailureStage.Page);
            Failure.HttpStatus.Should().Be(503);
            Failure.PagesRead.Should().Be(2);
        }

        [Test]
        public void It_stops_at_the_failed_page() => Host.Plan.Pages.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_the_projection_changes_on_page_2_twice : ReadFixture
    {
        protected override void Arrange(FaultPlan plan) =>
            plan.OnPage = (page, _) =>
                Task.FromResult(
                    page is { AttemptPage: 2, Attempt: <= 2 }
                        ? ReaderHost.ContractProblem("projection-changed")
                        : null
                );

        [Test]
        public void It_succeeds_with_every_item_after_two_restarts()
        {
            Success.Items.Should().Equal(ProjectionE2EFixture.SeededItems);
            Success.Restarts.Should().Be(2);
            Success.PageCount.Should().Be(4);
        }

        [Test]
        public void It_restarts_each_walk_from_its_first_page() =>
            Host
                .Plan.Pages.Select(page => (page.Attempt, page.AttemptPage))
                .Should()
                .Equal((1, 1), (1, 2), (2, 1), (2, 2), (3, 1), (3, 2), (3, 3), (3, 4));
    }

    [TestFixture]
    public class Given_the_projection_changes_on_page_2_four_times : ReadFixture
    {
        protected override void Arrange(FaultPlan plan) =>
            plan.OnPage = (page, _) =>
                Task.FromResult(
                    page.AttemptPage == 2 ? ReaderHost.ContractProblem("projection-changed") : null
                );

        [Test]
        public void It_fails_as_transient_projection_changed_after_three_restarts()
        {
            Failure.Category.Should().Be(FailureCategory.Transient);
            Failure.Code.Should().Be(FailureCode.ProjectionChanged);
            Failure.Restarts.Should().Be(3);
        }

        [Test]
        public void It_makes_four_walks() =>
            Host.Plan.Pages.Select(page => page.Attempt).Distinct().Should().Equal(1, 2, 3, 4);
    }

    [TestFixture]
    public class Given_a_transport_fault_on_page_2 : ReadFixture
    {
        protected override void Arrange(FaultPlan plan) =>
            plan.OnPage = (page, _) =>
                page.AttemptPage == 2
                    ? Task.FromException<HttpResponseMessage?>(
                        new HttpRequestException("Injected transport fault on page 2.")
                    )
                    : Task.FromResult<HttpResponseMessage?>(null);

        // A failure result carries no items: the type of the result is what exposes none.
        [Test]
        public void It_fails_with_no_items_as_a_transient_network_error_at_the_page_stage() =>
            Failure
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Category = FailureCategory.Transient,
                        Code = FailureCode.NetworkError,
                        Stage = FailureStage.Page,
                        HttpStatus = (int?)null,
                        PagesRead = 1,
                        Restarts = 0,
                    }
                );

        [Test]
        public void It_requests_no_page_after_the_faulted_one() =>
            Host.Plan.Pages.Select(page => (page.Attempt, page.AttemptPage)).Should().Equal((1, 1), (1, 2));
    }

    /// <summary>
    /// A store registered through the Configuration Service after DMS has loaded the tenant's catalog: a live read
    /// of the seeded store first makes DMS load that catalog, then the new store's first read is served, with no DMS
    /// restart between. This shows only that such a store is served on its first read. It claims nothing about how
    /// DMS finds it (a reload on the miss or an expiry-driven refresh) and nothing about timing; the API integration
    /// scenarios prove the mechanism.
    /// </summary>
    [TestFixture]
    public class Given_a_store_registered_after_dms_loaded_the_tenant_catalog
    {
        private ReaderHost _host = null!;
        private EducationOrganizationProjectionReadResult _catalogLoadingRead = null!;
        private EducationOrganizationProjectionReadResult _newStoreRead = null!;

        [OneTimeSetUp]
        public async Task ReadAsync()
        {
            ProjectionE2EFixture fixture = ProjectionE2EFixture.Current;
            _host = new ReaderHost(fixture);

            int seededStoreId = fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier).DataStoreId;
            _catalogLoadingRead = await _host.ReadAsync(
                ProjectionE2EFixture.SeededTenant,
                seededStoreId,
                await fixture.Services.DataStoreContextsAsync(
                    ProjectionE2EFixture.SeededTenant,
                    seededStoreId
                )
            );

            // The second tenant's provisioned database, empty, under a route context of its own.
            int newStoreId = await fixture.Services.CreateDataStoreAsync(
                ProjectionE2EFixture.SeededTenant,
                "Projection Reader E2E registered after catalog load",
                fixture.Environment.RouteThreeConnectionString,
                new Dictionary<string, string> { ["districtId"] = "255901", ["schoolYear"] = "2098" }
            );
            _newStoreRead = await _host.ReadAsync(
                ProjectionE2EFixture.SeededTenant,
                newStoreId,
                await fixture.Services.DataStoreContextsAsync(ProjectionE2EFixture.SeededTenant, newStoreId)
            );
        }

        [OneTimeTearDown]
        public void DisposeHost() => _host?.Dispose();

        [Test]
        public void It_reads_the_seeded_store_first_so_dms_has_loaded_the_tenant_catalog() =>
            _catalogLoadingRead
                .Should()
                .BeOfType<EducationOrganizationProjectionReadResult.Success>()
                .Subject.Items.Should()
                .Equal(ProjectionE2EFixture.SeededItems);

        [Test]
        public void It_serves_the_new_store_on_its_first_read_as_one_empty_page()
        {
            EducationOrganizationProjectionReadResult.Success success = _newStoreRead
                .Should()
                .BeOfType<EducationOrganizationProjectionReadResult.Success>()
                .Subject;
            success.Items.Should().BeEmpty();
            success.PageCount.Should().Be(1);
        }
    }

    /// <summary>
    /// DMS unreachable: the reader's base URL is a loopback listener, bound for the whole fixture, that accepts every
    /// connection and closes it at once. The port listens throughout, so no closed-port race decides the outcome, and
    /// no timer is involved.
    /// </summary>
    [TestFixture]
    public class Given_a_dms_base_url_that_closes_every_connection
    {
        private ClosingListener _listener = null!;
        private ReaderHost _host = null!;
        private EducationOrganizationProjectionReadResult _result = null!;

        [OneTimeSetUp]
        public async Task ReadAsync()
        {
            ProjectionE2EFixture fixture = ProjectionE2EFixture.Current;
            _listener = new ClosingListener();
            _host = new ReaderHost(fixture, dmsBaseUrl: _listener.BaseUrl);

            int dataStoreId = fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier).DataStoreId;
            _result = await _host.ReadAsync(
                ProjectionE2EFixture.SeededTenant,
                dataStoreId,
                await fixture.Services.DataStoreContextsAsync(ProjectionE2EFixture.SeededTenant, dataStoreId)
            );
        }

        [OneTimeTearDown]
        public async Task DisposeAsync()
        {
            _host?.Dispose();
            if (_listener is not null)
            {
                await _listener.DisposeAsync();
            }
        }

        // A failure result carries no items: the type of the result is what exposes none.
        [Test]
        public void It_fails_with_no_items_as_a_transient_network_error_at_discovery() =>
            _result
                .Should()
                .BeOfType<EducationOrganizationProjectionReadResult.Failure>()
                .Subject.Detail.Should()
                .BeEquivalentTo(
                    new
                    {
                        Category = FailureCategory.Transient,
                        Code = FailureCode.NetworkError,
                        Stage = FailureStage.Discovery,
                        HttpStatus = (int?)null,
                        PagesRead = 0,
                    }
                );

        [Test]
        public void It_sends_no_token_or_page_request() =>
            _host.Plan.Requests.Should().Equal(new RecordedRequest(RequestKind.Discovery, 0, 0));

        [Test]
        public void It_reaches_a_listening_endpoint() => _listener.Accepted.Should().BePositive();
    }

    /// <summary>A loopback TCP listener that accepts every connection and closes it at once, until disposed.</summary>
    private sealed class ClosingListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _accepted;

        public ClosingListener()
        {
            _listener.Start();
            BaseUrl = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _accepting = AcceptAsync();
        }

        public Uri BaseUrl { get; }

        /// <summary>The connections accepted so far; each is counted before it is closed.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptAsync()
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await _listener.AcceptSocketAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Interlocked.Increment(ref _accepted);
                socket.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accepting;
            _listener.Stop();
            _listener.Dispose();
            _stop.Dispose();
        }
    }

    [TestFixture]
    public class Given_the_caller_cancels_while_page_2_is_outstanding
    {
        private ReaderHost _host = null!;
        private readonly CancellationTokenSource _caller = new();
        private OperationCanceledException? _cancellation;
        private bool? _requestCancelledWithCaller;

        [OneTimeSetUp]
        public async Task ReadAsync()
        {
            ProjectionE2EFixture fixture = ProjectionE2EFixture.Current;
            _host = new ReaderHost(fixture);
            _host.Plan.OnPage = async (page, cancellationToken) =>
            {
                if (page.AttemptPage != 2)
                {
                    return null;
                }

                // CancelAsync completes after every registration has run, so a request token linked to the caller's
                // is cancelled by now. One that is not escapes at once rather than waiting for the page timeout, and
                // the observation below fails the case: no timing is involved either way.
                await _caller.CancelAsync();
                _requestCancelledWithCaller = cancellationToken.IsCancellationRequested;
                if (!cancellationToken.IsCancellationRequested)
                {
                    return ReaderHost.ContractProblem("target-unavailable");
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            };

            int dataStoreId = fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier).DataStoreId;
            try
            {
                await _host.ReadAsync(
                    ProjectionE2EFixture.SeededTenant,
                    dataStoreId,
                    await fixture.Services.DataStoreContextsAsync(
                        ProjectionE2EFixture.SeededTenant,
                        dataStoreId
                    ),
                    _caller.Token
                );
            }
            catch (OperationCanceledException exception)
            {
                _cancellation = exception;
            }
        }

        [OneTimeTearDown]
        public void Dispose()
        {
            _host?.Dispose();
            _caller.Dispose();
        }

        [Test]
        public void It_cancels_the_outstanding_page_request_when_the_caller_cancels() =>
            _requestCancelledWithCaller.Should().BeTrue();

        [Test]
        public void It_throws_with_the_callers_token() =>
            _cancellation
                .Should()
                .NotBeNull()
                .And.Subject.As<OperationCanceledException>()
                .CancellationToken.Should()
                .Be(_caller.Token);

        [Test]
        public void It_sends_no_page_after_the_cancelled_one() => _host.Plan.Pages.Should().HaveCount(2);
    }
}
