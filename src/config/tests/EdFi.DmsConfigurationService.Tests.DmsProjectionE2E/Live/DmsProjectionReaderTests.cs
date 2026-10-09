// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
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
            DbConnectionStringBuilder connection = new()
            {
                ConnectionString = Fixture.Environment.RouteTwoConnectionString,
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
            connection[databaseKey] = "edfi_dms_projection_reader_e2e_missing";

            _dataStoreId = await Fixture.Services.CreateDataStoreAsync(
                ProjectionE2EFixture.SeededTenant,
                "Projection Reader E2E missing database",
                connection.ConnectionString,
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
