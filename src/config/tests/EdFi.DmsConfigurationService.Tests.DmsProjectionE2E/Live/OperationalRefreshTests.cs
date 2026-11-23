// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>
/// The Configuration Service that <c>build-dms.ps1 InstanceE2ETest</c> recreated as an operational instance (job
/// workers, scheduler and reader on, one-minute refresh interval) refreshes the seeded tenant on its own schedule.
/// </summary>
public static class OperationalRefreshTests
{
    private const string Completed = "Completed";

    /// <summary>
    /// With no manual refresh until the last step: the tenant has an enabled schedule at the configured interval; a
    /// completed job linked to it publishes the seeded hierarchy; after a school is renamed through DMS, a completed job
    /// of a later occurrence publishes the new name; then a manual refresh completes with no source schedule. Every
    /// wait is bounded by three refresh intervals.
    /// <para>
    /// Ordered first: the reader fixtures register data stores in the seeded tenant that a refresh of every data store
    /// cannot complete, and they are removed only when the run ends. The set-up refuses to start if the tenant holds
    /// any data store besides the fixture's routes.
    /// </para>
    /// </summary>
    [TestFixture]
    [Order(1)]
    public class Given_the_operational_configuration_service_refreshes_the_seeded_tenant_on_schedule
    {
        private const long RenamedSchoolId = 100001;
        private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

        private CmsJobDatabase _database = null!;
        private int _intervalMinutes;
        private TimeSpan _deadline;
        private bool _renamed;

        private RefreshScheduleRow _schedule = null!;
        private RefreshJobRow _firstScheduledJob = null!;
        private IReadOnlyList<EducationOrganizationResponse> _firstPublication = [];
        private RefreshJobRow _laterScheduledJob = null!;
        private string _renamedName = null!;
        private string? _publishedRenamedName;
        private IReadOnlyList<RefreshJobRow> _unscheduledJobsBeforeManualRefresh = [];
        private string _manualStatus = null!;
        private RefreshJobRow? _manualJob;

        private static ProjectionE2EFixture Fixture => ProjectionE2EFixture.Current;

        private static FixtureRoute SeededRoute =>
            Fixture.Environment.Route(ProjectionE2EFixture.SeededQualifier);

        private static EducationOrganizationProjectionItem RenamedSchool =>
            ProjectionE2EFixture.SeededItems.Single(item => item.EducationOrganizationId == RenamedSchoolId);

        [OneTimeSetUp]
        public async Task RunTheScheduleAsync()
        {
            OperationalRefreshEnvironment environment = OperationalRefreshEnvironment.Read();
            _database = new CmsJobDatabase(
                Fixture.Environment.DatabaseEngine,
                environment.CmsReadOnlyConnectionString
            );
            _intervalMinutes = environment.RefreshIntervalMinutes;
            _deadline = TimeSpan.FromMinutes(3 * _intervalMinutes);

            await RequireOnlyTheFixtureRoutesAsync();

            // 1. The reconciler scheduled the tenant at the configured interval.
            _schedule = await PollAsync(
                $"the {ProjectionE2EFixture.SeededTenant} refresh schedule at {_intervalMinutes} minute(s)",
                async () =>
                {
                    RefreshScheduleRow? schedule = await _database.GetRefreshScheduleAsync(
                        ProjectionE2EFixture.SeededTenant
                    );
                    return (
                        schedule is { Enabled: true } && schedule.IntervalMinutes == _intervalMinutes,
                        schedule!,
                        $"schedule {schedule?.ToString() ?? "absent"}"
                    );
                }
            );

            // 2. A completed scheduled job published the seeded hierarchy.
            (_firstScheduledJob, _firstPublication) = await PollAsync(
                "a completed scheduled refresh publishing the seeded hierarchy",
                async () =>
                {
                    IReadOnlyList<EducationOrganizationResponse> published = await SeededStoreAsync();
                    IReadOnlyList<RefreshJobRow> jobs = await JobsAsync();
                    RefreshJobRow? latest = CompletedScheduledJobs(jobs).LastOrDefault();
                    return (
                        latest is not null && Shape(published).SequenceEqual(SeededShape()),
                        (latest!, published),
                        Describe(jobs, published)
                    );
                }
            );
            DateTime firstOccurrence = _firstScheduledJob.ScheduledOccurrence!.Value;

            // 3. A school renamed through DMS reaches the aggregate through a later occurrence.
            _renamedName = $"{RenamedSchool.NameOfInstitution} renamed";
            await WriteRenamedSchoolAsync(_renamedName);
            _renamed = true;
            (_laterScheduledJob, _publishedRenamedName) = await PollAsync(
                "a completed later scheduled refresh publishing the renamed school",
                async () =>
                {
                    IReadOnlyList<EducationOrganizationResponse> published = await SeededStoreAsync();
                    IReadOnlyList<RefreshJobRow> jobs = await JobsAsync();
                    RefreshJobRow? later = CompletedScheduledJobs(jobs)
                        .LastOrDefault(job => job.ScheduledOccurrence > firstOccurrence);
                    string? name = published
                        .SingleOrDefault(organization =>
                            organization.EducationOrganizationId == RenamedSchoolId
                        )
                        ?.NameOfInstitution;
                    return (
                        later is not null && name == _renamedName,
                        (later!, name),
                        Describe(jobs, published)
                    );
                }
            );
            _unscheduledJobsBeforeManualRefresh =
            [
                .. (await JobsAsync()).Where(job => job.SourceScheduleId is null),
            ];

            // 4. A manual refresh completes and is linked to no schedule.
            string jobId = await Fixture.Services.RequestRefreshOfEveryDataStoreAsync(
                ProjectionE2EFixture.SeededTenant
            );
            _manualStatus = await PollAsync(
                $"manual refresh job {jobId} to finish",
                async () =>
                {
                    string status = await Fixture.Services.GetJobStatusAsync(
                        ProjectionE2EFixture.SeededTenant,
                        jobId
                    );
                    return (status is Completed or "Error", status, $"status {status}");
                }
            );
            _manualJob = (await JobsAsync()).SingleOrDefault(job => job.JobId == jobId);
        }

        /// <summary>Restores the seeded name before the reader fixtures, which read the hierarchy as seeded.</summary>
        [OneTimeTearDown]
        public async Task RestoreTheSchoolNameAsync()
        {
            if (_renamed)
            {
                await WriteRenamedSchoolAsync(RenamedSchool.NameOfInstitution);
            }
        }

        [Test]
        public void It_keeps_an_enabled_schedule_at_the_configured_interval() =>
            _schedule.Should().BeEquivalentTo(new { Enabled = true, IntervalMinutes = _intervalMinutes });

        [Test]
        public void It_publishes_the_seeded_hierarchy_through_a_completed_job_of_the_schedule()
        {
            _firstScheduledJob
                .Should()
                .BeEquivalentTo(new { Status = Completed, SourceScheduleId = (long?)_schedule.Id });
            Shape(_firstPublication).Should().Equal(SeededShape());
        }

        [Test]
        public void It_publishes_the_renamed_school_through_a_later_occurrence_of_the_schedule()
        {
            _laterScheduledJob
                .Should()
                .BeEquivalentTo(new { Status = Completed, SourceScheduleId = (long?)_schedule.Id });
            _laterScheduledJob
                .ScheduledOccurrence.Should()
                .BeAfter(_firstScheduledJob.ScheduledOccurrence!.Value);
            _publishedRenamedName.Should().Be(_renamedName);
        }

        [Test]
        public void It_runs_no_refresh_the_schedule_did_not_enqueue_before_the_manual_request() =>
            _unscheduledJobsBeforeManualRefresh.Should().BeEmpty();

        [Test]
        public void It_completes_a_manual_refresh_that_no_schedule_enqueued()
        {
            _manualStatus.Should().Be(Completed);
            _manualJob
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = Completed,
                        SourceScheduleId = (long?)null,
                        ScheduledOccurrence = (DateTime?)null,
                    }
                );
        }

        private static async Task RequireOnlyTheFixtureRoutesAsync()
        {
            TenantDetailsResponse aggregate = await Fixture.Services.GetTenantAggregateAsync(
                ProjectionE2EFixture.SeededTenant
            );
            int?[] fixtureStores =
            [
                .. Fixture
                    .Environment.Routes.Where(route => route.Tenant == ProjectionE2EFixture.SeededTenant)
                    .Select(route => (int?)route.DataStoreId)
                    .Order(),
            ];
            int?[] tenantStores = [.. aggregate.DataStores.Select(store => store.Id).Order()];
            if (!tenantStores.SequenceEqual(fixtureStores))
            {
                throw new InvalidOperationException(
                    $"Setup failed: {ProjectionE2EFixture.SeededTenant} holds data stores "
                        + $"[{string.Join(", ", tenantStores)}], not only the fixture's routes "
                        + $"[{string.Join(", ", fixtureStores)}]. This fixture must run before the reader fixtures "
                        + "that register data stores in the tenant."
                );
            }
        }

        private static async Task<IReadOnlyList<EducationOrganizationResponse>> SeededStoreAsync()
        {
            TenantDetailsResponse aggregate = await Fixture.Services.GetTenantAggregateAsync(
                ProjectionE2EFixture.SeededTenant
            );
            return aggregate
                .DataStores.Single(store => store.Id == SeededRoute.DataStoreId)
                .EducationOrganizations;
        }

        private Task<IReadOnlyList<RefreshJobRow>> JobsAsync() =>
            _database.GetRefreshJobsAsync(ProjectionE2EFixture.SeededTenant);

        private IEnumerable<RefreshJobRow> CompletedScheduledJobs(IEnumerable<RefreshJobRow> jobs) =>
            jobs.Where(job => job.Status == Completed && job.SourceScheduleId == _schedule.Id)
                .OrderBy(job => job.ScheduledOccurrence);

        private static async Task WriteRenamedSchoolAsync(string name)
        {
            (string resource, JsonObject body) = ProjectionHierarchySeed.Document(RenamedSchool);
            body["nameOfInstitution"] = name;
            HttpStatusCode status = await Fixture.Services.PostResourceAsync(
                await Fixture.Services.ClientTokenAsync(Fixture.Seeder),
                SeededRoute,
                resource,
                body,
                allowExisting: true
            );
            if (status != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Writing school {RenamedSchoolId} answered {(int)status}, expected an update (200)."
                );
            }
        }

        private static IEnumerable<(long, string, string?, string, long?)> Shape(
            IEnumerable<EducationOrganizationResponse> organizations
        ) =>
            organizations.Select(organization =>
                (
                    organization.EducationOrganizationId,
                    organization.NameOfInstitution,
                    organization.ShortNameOfInstitution,
                    organization.Discriminator,
                    organization.ParentId
                )
            );

        private static IEnumerable<(long, string, string?, string, long?)> SeededShape() =>
            ProjectionE2EFixture.SeededItems.Select(item =>
                (
                    item.EducationOrganizationId,
                    item.NameOfInstitution,
                    item.ShortNameOfInstitution,
                    item.Discriminator,
                    item.ParentId
                )
            );

        private static string Describe(
            IReadOnlyList<RefreshJobRow> jobs,
            IReadOnlyList<EducationOrganizationResponse> published
        ) =>
            $"refresh jobs [{string.Join("; ", jobs.TakeLast(10))}]; published in {SeededRoute.Qualifier}: "
            + $"[{string.Join(", ", published.Select(organization => $"{organization.EducationOrganizationId} {organization.NameOfInstitution}"))}]";

        /// <summary>
        /// Probes every <see cref="_pollInterval"/> until the probe reports done or <see cref="_deadline"/> passes, then
        /// fails naming what it waited for and its last observation. The value is read only from a done probe.
        /// </summary>
        private async Task<T> PollAsync<T>(
            string awaited,
            Func<Task<(bool Done, T Value, string Observed)>> probe
        )
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                (bool done, T value, string observed) = await probe();
                if (done)
                {
                    return value;
                }

                if (elapsed.Elapsed >= _deadline)
                {
                    throw new InvalidOperationException(
                        $"Waited {_deadline.TotalMinutes} minutes for {awaited}; last observed: {observed}"
                    );
                }

                await Task.Delay(_pollInterval);
            }
        }
    }
}
