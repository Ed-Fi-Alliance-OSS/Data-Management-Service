// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Codes = EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh.EducationOrganizationRefreshJobErrorCodes;
using FailureCode = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using ProjectionCodes = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionJobErrorCodes;
using ReadResult = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionReadResult;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationRefreshJobHandlerTests
{
    private const int FinalAttempt = HandlerHarness.MaxAttempts;

    private static void ShouldBeComposite(Exception? exception, params string[] categories)
    {
        JobPermanentException permanent = exception.Should().BeOfType<JobPermanentException>().Subject;
        permanent.ErrorCode.Code.Should().Be(Codes.Incomplete);
        permanent.CategoryCodes.Should().Equal(categories);
    }

    private static void ShouldBeSingle(Exception? exception, string code)
    {
        JobPermanentException permanent = exception.Should().BeOfType<JobPermanentException>().Subject;
        permanent.ErrorCode.Code.Should().Be(code);
        permanent.CategoryCodes.Should().BeNull();
    }

    /// <summary>A transient failure of one data store, from each source the decision table names.</summary>
    public enum Transient
    {
        Busy,
        LeaseLost,
        DbFailure,
        ReadTimeout,
    }

    private static StoreScript TransientScript(Transient transient) =>
        transient switch
        {
            Transient.Busy => StoreScript.Busy,
            Transient.LeaseLost => StoreScript.LeaseLost,
            Transient.DbFailure => StoreScript.AcquireDbFailure,
            Transient.ReadTimeout => StoreScript.ReadFails(FailureCode.Timeout),
            _ => throw new ArgumentOutOfRangeException(nameof(transient)),
        };

    [TestFixture]
    public class Given_a_refresh_of_all_whose_data_stores_all_refresh
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness(
                (3, StoreScript.Refreshes),
                (10, StoreScript.Refreshes),
                (21, StoreScript.Refreshes)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All);
        }

        [Test]
        public void It_completes() => _exception.Should().BeNull();

        [Test]
        public void It_asks_for_every_data_store_of_the_tenant() =>
            _harness.Calls[0].Should().Be("Targets:all");

        [Test]
        public void It_refreshes_every_data_store_in_the_order_given() =>
            _harness
                .Steps("Acquire", "Read", "Activate")
                .Should()
                .Equal(
                    "Acquire:3",
                    "Read:3",
                    "Activate:3",
                    "Acquire:10",
                    "Read:10",
                    "Activate:10",
                    "Acquire:21",
                    "Read:21",
                    "Activate:21"
                );

        [Test]
        public void It_reads_in_the_jobs_tenant() =>
            _harness.ReadTenantNames.Should().Equal("Tenant_7", "Tenant_7", "Tenant_7");

        [Test]
        public void It_logs_the_completion()
        {
            LogEntry finished = _harness.Finished;
            finished.Level.Should().Be(LogLevel.Information);
            finished.Field("Decision").Should().Be("Completed");
            finished.Field("TenantId").Should().Be(7L);
            finished.Field("Scope").Should().Be("All");
            finished.Field("Targets").Should().Be(3);
            finished.Field("Refreshed").Should().Be(3);
            finished.Field("Removed").Should().Be(0);
            finished.Field("Failed").Should().Be(0);
            finished.Field("JobErrorCode").Should().BeNull();
            finished.Field("TargetLookupFailed").Should().Be(false);
        }
    }

    [TestFixture]
    public class Given_a_refresh_of_all_in_a_tenant_without_data_stores
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness();
            _exception = await _harness.RunAsync(HandlerHarness.All);
        }

        [Test]
        public void It_completes() => _exception.Should().BeNull();

        [Test]
        public void It_refreshes_nothing() => _harness.Calls.Should().Equal("Targets:all");

        [Test]
        public void It_logs_the_completion_with_no_targets()
        {
            _harness.Finished.Field("Decision").Should().Be("Completed");
            _harness.Finished.Field("Targets").Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_data_store_that_fails_permanently_between_two_that_refresh
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness(
                (1, StoreScript.Refreshes),
                (2, StoreScript.ReadFails(FailureCode.TargetSchemaIncompatible)),
                (3, StoreScript.Refreshes)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: 1);
        }

        [Test]
        public void It_fails_with_the_composite_without_retrying() =>
            ShouldBeComposite(_exception, ProjectionCodes.TargetSchemaIncompatible);

        [Test]
        public void It_attempts_the_data_store_after_the_failed_one() =>
            _harness
                .Steps("Acquire", "Read", "Activate", "Release")
                .Should()
                .Equal(
                    "Acquire:1",
                    "Read:1",
                    "Activate:1",
                    "Acquire:2",
                    "Read:2",
                    "Release:2",
                    "Acquire:3",
                    "Read:3",
                    "Activate:3"
                );

        [Test]
        public void It_logs_the_failure()
        {
            LogEntry finished = _harness.Finished;
            finished.Level.Should().Be(LogLevel.Warning);
            finished.Field("Decision").Should().Be("Failed");
            finished.Field("Refreshed").Should().Be(2);
            finished.Field("Failed").Should().Be(1);
            finished.Field("JobErrorCode").Should().Be(Codes.Incomplete);
        }
    }

    [TestFixture(Transient.Busy)]
    [TestFixture(Transient.LeaseLost)]
    [TestFixture(Transient.DbFailure)]
    [TestFixture(Transient.ReadTimeout)]
    public class Given_a_transient_failure_while_attempts_remain(Transient transient)
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness(
                (1, StoreScript.Refreshes),
                (2, TransientScript(transient)),
                (3, StoreScript.ReadFails(FailureCode.Forbidden)),
                (4, StoreScript.Refreshes)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: FinalAttempt - 1);
        }

        [Test]
        public void It_retries_even_with_a_permanent_failure_beside_it() =>
            _exception.Should().BeOfType<EducationOrganizationRefreshRetryException>();

        [Test]
        public void It_attempts_every_data_store() =>
            _harness.Steps("Acquire").Should().Equal("Acquire:1", "Acquire:2", "Acquire:3", "Acquire:4");

        [Test]
        public void It_keeps_the_publications() =>
            _harness.Steps("Activate").Should().Equal("Activate:1", "Activate:4");

        [Test]
        public void It_logs_the_retry()
        {
            LogEntry finished = _harness.Finished;
            finished.Level.Should().Be(LogLevel.Warning);
            finished.Field("Decision").Should().Be("Retry");
            finished.Field("Failed").Should().Be(2);
            finished.Field("JobErrorCode").Should().BeNull();
        }
    }

    [TestFixture(Transient.Busy, Codes.TargetBusy)]
    [TestFixture(Transient.LeaseLost, Codes.TemporarilyUnavailable)]
    [TestFixture(Transient.DbFailure, Codes.TemporarilyUnavailable)]
    [TestFixture(Transient.ReadTimeout, Codes.TemporarilyUnavailable)]
    public class Given_a_transient_failure_on_the_final_attempt(Transient transient, string category)
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness((1, StoreScript.Refreshes), (2, TransientScript(transient)));
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: FinalAttempt);
        }

        [Test]
        public void It_fails_with_the_composite_naming_its_category() =>
            ShouldBeComposite(_exception, category);

        [Test]
        public void It_keeps_the_publication() => _harness.Steps("Activate").Should().Equal("Activate:1");
    }

    [TestFixture]
    public class Given_mixed_failures_on_the_final_attempt
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness(
                (1, StoreScript.Busy),
                (2, StoreScript.ReadFails(FailureCode.Forbidden)),
                (3, StoreScript.ReadFails(FailureCode.Timeout)),
                (4, StoreScript.ReadFails(FailureCode.Forbidden)),
                (5, StoreScript.Busy),
                (6, StoreScript.Refreshes),
                (7, StoreScript.LeaseLost),
                (8, StoreScript.Removed)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: FinalAttempt);
        }

        [Test]
        public void It_lists_each_category_once_in_the_order_of_the_first_data_store_that_produced_it() =>
            ShouldBeComposite(
                _exception,
                Codes.TargetBusy,
                ProjectionCodes.Forbidden,
                Codes.TemporarilyUnavailable
            );

        [Test]
        public void It_attempts_every_data_store() =>
            _harness
                .Steps("Acquire")
                .Should()
                .Equal(Enumerable.Range(1, 8).Select(dataStoreId => $"Acquire:{dataStoreId}"));

        [Test]
        public void It_logs_the_counts()
        {
            LogEntry finished = _harness.Finished;
            finished.Field("Targets").Should().Be(8);
            finished.Field("Refreshed").Should().Be(1);
            finished.Field("Removed").Should().Be(1);
            finished.Field("Failed").Should().Be(6);
        }
    }

    [TestFixture]
    public class Given_every_problem_category_on_the_final_attempt
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;
        private ServiceProvider _provider = null!;

        [SetUp]
        public async Task Setup()
        {
            // Each permanent projection code is produced by the failure code of the same name.
            IEnumerable<StoreScript> scripts = ProjectionCodes
                .All.Select(pair =>
                    StoreScript.ReadFails(Enum.Parse<FailureCode>(pair.Key["EdOrgProjection".Length..]))
                )
                .Append(StoreScript.ReadFails(FailureCode.NetworkError))
                .Append(StoreScript.Busy)
                .Append(StoreScript.ReadFails(FailureCode.Forbidden));
            _harness = new HandlerHarness([.. scripts.Select((script, index) => (index + 1, script))]);
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: FinalAttempt);

            ServiceCollection services = new();
            services.AddEducationOrganizationRefresh(new ConfigurationBuilder().Build());
            _provider = services.BuildServiceProvider();
        }

        [TearDown]
        public void TearDown() => _provider.Dispose();

        [Test]
        public void It_lists_all_fifteen_allowed_categories() =>
            ShouldBeComposite(_exception, [.. Codes.IncompleteCategories]);

        [Test]
        public void It_composes_every_category_code_within_the_limit()
        {
            IReadOnlyList<string> categories = ((JobPermanentException)_exception!).CategoryCodes!;
            IJobErrorCodeRegistry registry = _provider.GetRequiredService<IJobErrorCodeRegistry>();

            registry.TryCompose(Codes.Incomplete, categories, out string? message).Should().BeTrue();

            message!.Length.Should().BeLessThanOrEqualTo(1000);
            message
                .Should()
                .StartWith(
                    $"{Codes.IncompleteMessage} Problem categories: {string.Join(", ", Codes.IncompleteCategories)}."
                );
        }
    }

    [TestFixture]
    public class Given_a_refresh_of_all_in_which_a_data_store_was_deleted
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness(
                (1, StoreScript.Refreshes),
                (2, StoreScript.Removed),
                (3, StoreScript.Refreshes)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: 1);
        }

        [Test]
        public void It_completes() => _exception.Should().BeNull();

        [Test]
        public void It_refreshes_the_others() =>
            _harness.Steps("Activate").Should().Equal("Activate:1", "Activate:3");

        [Test]
        public void It_logs_the_removal()
        {
            _harness.Finished.Field("Removed").Should().Be(1);
            _harness.Finished.Field("Failed").Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_refresh_of_one_data_store
    {
        private const int DataStoreId = 42;

        public enum Case
        {
            Refreshes,
            Missing,
            RemovedAtAcquisition,
            RemovedLater,
            Permanent,
            BusyWithAttemptsLeft,
            TransientWithAttemptsLeft,
            BusyOnFinalAttempt,
            LeaseLostOnFinalAttempt,
            TransientOnFinalAttempt,
        }

        private static async Task<(HandlerHarness Harness, Exception? Exception)> RunAsync(Case testCase)
        {
            StoreScript? script = testCase switch
            {
                Case.Refreshes => StoreScript.Refreshes,
                Case.Missing => null,
                Case.RemovedAtAcquisition => StoreScript.Removed,
                Case.RemovedLater => new StoreScript(Reclaim: new SnapshotTargetResult.DataStoreRemoved()),
                Case.Permanent => StoreScript.ReadFails(FailureCode.TargetSchemaIncompatible),
                Case.BusyWithAttemptsLeft or Case.BusyOnFinalAttempt => StoreScript.Busy,
                Case.LeaseLostOnFinalAttempt => StoreScript.LeaseLost,
                _ => StoreScript.ReadFails(FailureCode.ServiceUnavailable),
            };
            int attempt = testCase.ToString().EndsWith("OnFinalAttempt") ? FinalAttempt : 1;
            HandlerHarness harness = script is null
                ? new HandlerHarness()
                : new HandlerHarness((DataStoreId, script));
            Exception? exception = await harness.RunAsync(HandlerHarness.One(DataStoreId), attempt);
            return (harness, exception);
        }

        [Test]
        public async Task It_completes_when_the_data_store_refreshes()
        {
            (HandlerHarness harness, Exception? exception) = await RunAsync(Case.Refreshes);
            exception.Should().BeNull();
            harness.Calls[0].Should().Be($"Targets:{DataStoreId}");
            harness.Steps("Activate").Should().Equal($"Activate:{DataStoreId}");
            harness.Finished.Field("Scope").Should().Be("One");
            harness.Finished.Field("DataStoreId").Should().Be(DataStoreId);
        }

        [TestCase(Case.Missing)]
        [TestCase(Case.RemovedAtAcquisition)]
        [TestCase(Case.RemovedLater)]
        public async Task It_fails_without_retrying_when_the_data_store_is_missing(Case testCase)
        {
            (HandlerHarness harness, Exception? exception) = await RunAsync(testCase);
            ShouldBeSingle(exception, Codes.DataStoreNotFound);
            harness.Steps("Activate").Should().BeEmpty();
        }

        [Test]
        public async Task It_does_not_refresh_a_missing_data_store()
        {
            (HandlerHarness harness, _) = await RunAsync(Case.Missing);
            harness.Calls.Should().Equal($"Targets:{DataStoreId}");
        }

        [Test]
        public async Task It_fails_with_the_single_code_of_a_permanent_failure()
        {
            (_, Exception? exception) = await RunAsync(Case.Permanent);
            ShouldBeSingle(exception, ProjectionCodes.TargetSchemaIncompatible);
        }

        [TestCase(Case.BusyWithAttemptsLeft)]
        [TestCase(Case.TransientWithAttemptsLeft)]
        public async Task It_retries_a_transient_failure_while_attempts_remain(Case testCase)
        {
            (_, Exception? exception) = await RunAsync(testCase);
            exception.Should().BeOfType<EducationOrganizationRefreshRetryException>();
        }

        [TestCase(Case.BusyOnFinalAttempt, Codes.TargetBusy)]
        [TestCase(Case.LeaseLostOnFinalAttempt, Codes.TemporarilyUnavailable)]
        [TestCase(Case.TransientOnFinalAttempt, Codes.TemporarilyUnavailable)]
        public async Task It_fails_with_the_single_transient_code_on_the_final_attempt(
            Case testCase,
            string code
        )
        {
            (_, Exception? exception) = await RunAsync(testCase);
            ShouldBeSingle(exception, code);
        }
    }

    [TestFixture(EducationOrganizationRefreshScope.All, 1, null)]
    [TestFixture(EducationOrganizationRefreshScope.One, 1, null)]
    [TestFixture(EducationOrganizationRefreshScope.All, FinalAttempt, Codes.Incomplete)]
    [TestFixture(EducationOrganizationRefreshScope.One, FinalAttempt, Codes.TemporarilyUnavailable)]
    public class Given_the_target_lookup_fails(
        EducationOrganizationRefreshScope scope,
        int attempt,
        string? errorCode
    )
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness((1, StoreScript.Refreshes))
            {
                Targets = new SnapshotRefreshTargetsResult.DbFailure(HandlerHarness.Diagnostic),
            };
            _exception = await _harness.RunAsync(
                scope == EducationOrganizationRefreshScope.All ? HandlerHarness.All : HandlerHarness.One(1),
                attempt
            );
        }

        [Test]
        public void It_retries_or_fails_as_unavailable()
        {
            switch (errorCode)
            {
                case null:
                    _exception.Should().BeOfType<EducationOrganizationRefreshRetryException>();
                    break;
                case Codes.Incomplete:
                    ShouldBeComposite(_exception, Codes.TemporarilyUnavailable);
                    break;
                default:
                    ShouldBeSingle(_exception, errorCode);
                    break;
            }
        }

        [Test]
        public void It_refreshes_nothing() => _harness.Calls.Should().HaveCount(1);

        [Test]
        public void It_logs_the_lookup_diagnostic()
        {
            LogEntry finished = _harness.Finished;
            finished.Field("TargetLookupFailed").Should().Be(true);
            finished.Field("ExceptionTypeChain").Should().Be(HandlerHarness.Diagnostic.ExceptionTypeChain);
            finished.Field("ProviderErrorCode").Should().Be(HandlerHarness.Diagnostic.ProviderErrorCode);
            finished.Field("FailedOperation").Should().Be(HandlerHarness.Diagnostic.Operation);
        }
    }

    [TestFixture]
    public class Given_a_retried_refresh
    {
        private HandlerHarness _harness = null!;
        private Exception? _firstAttempt;
        private Exception? _secondAttempt;
        private int _firstAttemptCalls;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness(
                (1, StoreScript.Refreshes),
                (2, StoreScript.Busy),
                (3, StoreScript.Refreshes)
            );
            _firstAttempt = await _harness.RunAsync(HandlerHarness.All, attempt: 1);
            _firstAttemptCalls = _harness.Calls.Count;
            _harness.Stores[2] = StoreScript.Refreshes;
            _secondAttempt = await _harness.RunAsync(HandlerHarness.All, attempt: 2);
        }

        [Test]
        public void It_retries_then_completes()
        {
            _firstAttempt.Should().BeOfType<EducationOrganizationRefreshRetryException>();
            _secondAttempt.Should().BeNull();
        }

        [Test]
        public void It_reads_every_data_store_again_including_those_already_published() =>
            _harness
                .Calls.Skip(_firstAttemptCalls)
                .Where(call => call.StartsWith("Read:", StringComparison.Ordinal))
                .Should()
                .Equal("Read:1", "Read:2", "Read:3");
    }

    [TestFixture]
    public class Given_the_caller_cancels_after_a_data_store_publishes
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource caller = new();
            _harness = new HandlerHarness(
                (1, StoreScript.Refreshes with { OnActivate = caller.Cancel }),
                (2, StoreScript.Refreshes)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: 1, caller.Token);
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_starts_no_further_data_store() =>
            _harness.Steps("Acquire").Should().Equal("Acquire:1");

        [Test]
        public void It_decides_nothing() => _harness.FinishedEntries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_caller_cancels_during_a_read
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource caller = new();
            _harness = new HandlerHarness(
                (1, StoreScript.Refreshes with { OnRead = caller.Cancel }),
                (2, StoreScript.Refreshes)
            );
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: 1, caller.Token);
        }

        [Test]
        public void It_propagates_the_cancellation() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();

        [Test]
        public void It_releases_and_starts_no_further_data_store() =>
            _harness.Steps("Acquire", "Activate", "Release").Should().Equal("Acquire:1", "Release:1");

        [Test]
        public void It_decides_nothing() => _harness.FinishedEntries.Should().BeEmpty();
    }

    public enum FenceRefusal
    {
        LeaseLost,
        Unavailable,
    }

    [TestFixture(FenceRefusal.LeaseLost)]
    [TestFixture(FenceRefusal.Unavailable)]
    public class Given_a_fence_that_refuses_an_activation(FenceRefusal refusal)
    {
        private HandlerHarness _harness = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            _harness = new HandlerHarness((1, StoreScript.Refreshes), (2, StoreScript.Refreshes));
            _harness.Fence.Refusal =
                refusal == FenceRefusal.LeaseLost
                    ? new JobLeaseLostException()
                    : new JobFenceUnavailableException();
            _exception = await _harness.RunAsync(HandlerHarness.All, attempt: 1);
        }

        [Test]
        public void It_propagates_the_refusal() =>
            _exception!
                .GetType()
                .Should()
                .Be(
                    refusal == FenceRefusal.LeaseLost
                        ? typeof(JobLeaseLostException)
                        : typeof(JobFenceUnavailableException)
                );

        [Test]
        public void It_starts_no_further_data_store() =>
            _harness.Steps("Acquire").Should().Equal("Acquire:1");

        [Test]
        public void It_decides_nothing() => _harness.FinishedEntries.Should().BeEmpty();
    }

    [TestFixture(true)]
    [TestFixture(false)]
    public class Given_a_refresh_job_run_by_the_executor(bool multiTenancy)
    {
        private HandlerHarness _refresh = null!;
        private ExecutorHarness _executor = null!;
        private readonly ConcurrentQueue<TenantContext> _tenantsAtResolution = new();
        private JobExecutionResult _result = null!;

        private TenantContext JobTenant =>
            multiTenancy ? new TenantContext.Multitenant(7, "Tenant_7") : new TenantContext.NotMultitenant();

        [SetUp]
        public async Task Setup()
        {
            _refresh = new HandlerHarness(
                (1, StoreScript.Refreshes),
                (2, StoreScript.ReadFails(FailureCode.TargetSchemaIncompatible)),
                (3, StoreScript.Refreshes)
            );
            _executor = new ExecutorHarness(
                multiTenancy,
                configure: services =>
                {
                    services.AddLogging();
                    services.AddSingleton(Options.Create(_refresh.JobSettings));
                    services.AddEducationOrganizationRefresh(new ConfigurationBuilder().Build());
                    services.AddTransient(provider =>
                    {
                        _tenantsAtResolution.Enqueue(
                            provider.GetRequiredService<ITenantContextProvider>().Context
                        );
                        return _refresh.Repository;
                    });
                    services.AddTransient(_ => _refresh.Reader);
                }
            );
            _executor.Tenants.Tenants[7] = "Tenant_7";

            _result = await _executor.Executor.ExecuteAsync(
                ExecutorHarness.Job(
                    jobType: EducationOrganizationRefreshJobTypes.Refresh,
                    payloadJson: """{"scope":1,"dataStoreId":0}""",
                    tenantId: multiTenancy ? 7 : null
                ),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _executor.Dispose();

        [Test]
        public void It_resolves_the_repository_after_the_jobs_tenant_is_installed() =>
            _tenantsAtResolution.Should().NotBeEmpty().And.OnlyContain(tenant => tenant == JobTenant);

        [Test]
        public void It_reads_in_the_jobs_tenant() =>
            _refresh.ReadTenantNames.Should().Equal(Enumerable.Repeat(multiTenancy ? "Tenant_7" : null, 3));

        [Test]
        public void It_publishes_the_data_stores_that_refreshed() =>
            _refresh.Steps("Activate").Should().Equal("Activate:1", "Activate:3");

        [Test]
        public void It_records_the_composite_from_registered_text()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: Codes.Incomplete));
            string categoryMessage = ProjectionCodes
                .All.Single(pair => pair.Key == ProjectionCodes.TargetSchemaIncompatible)
                .Value;
            _executor
                .Leases.OutcomeWrites.Should()
                .Equal(
                    $"FailTerminal:{Codes.Incomplete}:{Codes.IncompleteMessage} Problem categories: "
                        + $"{ProjectionCodes.TargetSchemaIncompatible}. {categoryMessage}"
                );
        }

        [Test]
        public void It_runs_without_an_http_context() =>
            _executor.Services.GetService<IHttpContextAccessor>().Should().BeNull();
    }
}

/// <summary>How one scripted data store's refresh goes; by default every step succeeds.</summary>
public sealed record StoreScript(
    SnapshotAcquireResult? Acquire = null,
    SnapshotTargetResult? Reclaim = null,
    ReadResult? Read = null,
    Action? OnRead = null,
    Action? OnActivate = null
)
{
    public static StoreScript Refreshes => new();

    public static StoreScript Busy => new(Acquire: new SnapshotAcquireResult.Busy());

    public static StoreScript Removed => new(Acquire: new SnapshotAcquireResult.DataStoreNotFound());

    public static StoreScript AcquireDbFailure =>
        new(Acquire: new SnapshotAcquireResult.DbFailure(HandlerHarness.Diagnostic));

    public static StoreScript LeaseLost =>
        new(Reclaim: new SnapshotTargetResult.LeaseLost(SnapshotLeaseLossReason.Taken));

    public static StoreScript ReadFails(FailureCode code) =>
        new(
            Read: new ReadResult.Failure(
                EducationOrganizationProjectionFailure.Create(code, EducationOrganizationProjectionStage.Page)
            )
        );
}

/// <summary>A fence that runs the work in a fake transaction, unless told to refuse.</summary>
internal sealed class HandlerFence : IJobFence
{
    public Exception? Refusal { get; set; }

    public async Task ExecuteAsync(
        Func<DbTransaction, CancellationToken, Task> work,
        CancellationToken cancellationToken
    )
    {
        if (Refusal is not null)
        {
            throw Refusal;
        }
        await work(A.Fake<DbTransaction>(), cancellationToken);
    }
}

/// <summary>
/// An <see cref="EducationOrganizationRefreshJobHandler"/> over the real target refresher, with FakeItEasy fakes of
/// the repository and reader scripted per data store. Every target lookup, acquisition, read, activation and release
/// is recorded in order with its data store.
/// </summary>
internal sealed class HandlerHarness
{
    public const int MaxAttempts = 3;

    public static readonly JobFailureDiagnostic Diagnostic = new(
        "Npgsql.NpgsqlException",
        "08006",
        "TestOperation"
    );

    public static readonly EducationOrganizationRefreshPayload All = new(
        EducationOrganizationRefreshScope.All,
        0
    );

    private readonly ConcurrentQueue<string> _calls = new();
    private readonly ConcurrentQueue<string?> _readTenantNames = new();

    public HandlerHarness(params (int DataStoreId, StoreScript Script)[] stores)
    {
        foreach ((int dataStoreId, StoreScript script) in stores)
        {
            Stores[dataStoreId] = script;
        }
        Targets = new SnapshotRefreshTargetsResult.Success([
            .. stores.Select(store => new EducationOrganizationRefreshTarget(
                store.DataStoreId,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["schoolYear"] = "2026" }
            )),
        ]);

        Repository = A.Fake<IEducationOrganizationSnapshotRepository>();
        Reader = A.Fake<IEducationOrganizationProjectionReader>();
        Arrange();

        Handler = new EducationOrganizationRefreshJobHandler(
            Repository,
            new EducationOrganizationTargetRefresher(
                Repository,
                Reader,
                Options.Create(JobSettings),
                new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)),
                RefresherLogger
            ),
            Logger
        );
    }

    public static EducationOrganizationRefreshPayload One(int dataStoreId) =>
        new(EducationOrganizationRefreshScope.One, dataStoreId);

    public Dictionary<int, StoreScript> Stores { get; } = [];

    public SnapshotRefreshTargetsResult Targets { get; init; }

    public JobOptions JobSettings { get; } =
        new() { LeaseDuration = TimeSpan.FromMinutes(5), RenewalInterval = TimeSpan.FromMinutes(1) };

    public IEducationOrganizationSnapshotRepository Repository { get; }

    public IEducationOrganizationProjectionReader Reader { get; }

    public HandlerFence Fence { get; } = new();

    public TenantContext Tenant { get; init; } = new TenantContext.Multitenant(7, "Tenant_7");

    public CapturingLogger<EducationOrganizationRefreshJobHandler> Logger { get; } = new();

    public CapturingLogger<EducationOrganizationTargetRefresher> RefresherLogger { get; } = new();

    public EducationOrganizationRefreshJobHandler Handler { get; }

    public IReadOnlyList<string> Calls => [.. _calls];

    public IReadOnlyList<string?> ReadTenantNames => [.. _readTenantNames];

    public IEnumerable<LogEntry> FinishedEntries =>
        Logger.Entries.Where(entry => entry.EventId.Name == "EducationOrganizationRefreshFinished");

    public LogEntry Finished => FinishedEntries.Single();

    /// <summary>The recorded calls of the named kinds, in order.</summary>
    public IReadOnlyList<string> Steps(params string[] kinds) =>
        [.. Calls.Where(call => kinds.Contains(call[..call.IndexOf(':')]))];

    public async Task<Exception?> RunAsync(
        EducationOrganizationRefreshPayload payload,
        int attempt = 1,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await Handler.ExecuteAsync(
                new JobExecutionContext("job-11", Tenant, attempt, MaxAttempts, Fence),
                payload,
                cancellationToken
            );
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private void Arrange()
    {
        A.CallTo(() => Repository.GetRefreshTargetsAsync(A<int?>._, A<CancellationToken>._))
            .ReturnsLazily(
                (int? dataStoreId, CancellationToken _) =>
                {
                    _calls.Enqueue($"Targets:{dataStoreId?.ToString() ?? "all"}");
                    return Task.FromResult(Targets);
                }
            );
        A.CallTo(() => Repository.AcquireAsync(A<int>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily(
                (int dataStoreId, string owner, CancellationToken _) =>
                {
                    _calls.Enqueue($"Acquire:{dataStoreId}");
                    return Task.FromResult(
                        Stores[dataStoreId].Acquire
                            ?? new SnapshotAcquireResult.Acquired(
                                new EducationOrganizationSnapshotLease(dataStoreId, owner, 2)
                            )
                    );
                }
            );
        A.CallTo(() =>
                Repository.ReclaimAsync(A<EducationOrganizationSnapshotLease>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (EducationOrganizationSnapshotLease lease, CancellationToken _) =>
                    Task.FromResult(Stores[lease.DataStoreId].Reclaim ?? new SnapshotTargetResult.Success(0))
            );
        A.CallTo(() => Repository.RenewAsync(A<EducationOrganizationSnapshotLease>._, A<CancellationToken>._))
            .Returns(Task.FromResult<SnapshotTargetResult>(new SnapshotTargetResult.Success(1)));
        A.CallTo(() =>
                Repository.StageAsync(
                    A<EducationOrganizationSnapshotLease>._,
                    A<IReadOnlyList<EducationOrganizationProjectionItem>>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult<SnapshotTargetResult>(new SnapshotTargetResult.Success(1)));
        A.CallTo(() =>
                Repository.ActivateAsync(
                    A<DbTransaction>._,
                    A<EducationOrganizationSnapshotLease>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (DbTransaction _, EducationOrganizationSnapshotLease lease, string _, CancellationToken _) =>
                {
                    _calls.Enqueue($"Activate:{lease.DataStoreId}");
                    Stores[lease.DataStoreId].OnActivate?.Invoke();
                    return Task.FromResult<SnapshotTargetResult>(new SnapshotTargetResult.Success(1));
                }
            );
        A.CallTo(() => Repository.PruneAsync(A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult<SnapshotTargetResult>(new SnapshotTargetResult.Success(0)));
        A.CallTo(() =>
                Repository.ReleaseAsync(A<EducationOrganizationSnapshotLease>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (EducationOrganizationSnapshotLease lease, CancellationToken _) =>
                {
                    _calls.Enqueue($"Release:{lease.DataStoreId}");
                    return Task.FromResult<SnapshotTargetResult>(new SnapshotTargetResult.Success(1));
                }
            );
        A.CallTo(() =>
                Reader.ReadAllAsync(A<EducationOrganizationProjectionReadRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (EducationOrganizationProjectionReadRequest request, CancellationToken _) =>
                {
                    _calls.Enqueue($"Read:{request.DataStoreId}");
                    _readTenantNames.Enqueue(request.TenantName);
                    StoreScript script = Stores[request.DataStoreId];
                    script.OnRead?.Invoke();
                    return Task.FromResult(
                        script.Read
                            ?? new ReadResult.Success(
                                [new(255901, "Grand Bend ISD", null, "edfi.LocalEducationAgency", null)],
                                "1.0.0",
                                1,
                                0
                            )
                    );
                }
            );
    }
}
