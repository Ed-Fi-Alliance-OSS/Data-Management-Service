// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobExecutorTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private static JobWriteResult.FailureUnknown FailureUnknown() =>
        new(new JobFailureDiagnostic("System.TimeoutException", null, "Renew"));

    private static JobWriteResult.ResultUnknown ResultUnknown() =>
        new(new JobFailureDiagnostic("System.TimeoutException", null, "Complete"));

    [TestFixture]
    public class Given_a_handler_that_completes
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_completes_the_job_with_one_outcome_write()
        {
            _result.Should().Be(new JobExecutionResult(JobExecutionOutcome.Completed));
            _harness.Leases.OutcomeWrites.Should().Equal("Complete");
        }

        [Test]
        public void It_runs_the_handler_in_a_scope_of_its_own_and_disposes_it()
        {
            _harness.Scopes.Created.Should().Be(1);
            _harness.Scopes.Disposed.Should().Be(1);
        }

        [Test]
        public void It_installs_the_single_tenant_context_before_the_handler_is_resolved() =>
            _harness.Script.TenantAtResolution.Should().Be(new TenantContext.NotMultitenant());

        [Test]
        public void It_logs_the_claim_and_the_completion() =>
            _harness
                .Logger.Entries.Select(entry => entry.EventId.Name)
                .Should()
                .Equal("JobClaimed", "JobCompleted");
    }

    [TestFixture]
    public class Given_a_handler_that_outlives_a_renewal_interval
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            TaskCompletionSource finish = ScriptedLeaseRepository.NewSignal();
            _harness.Script.Run = (_, _, _) => finish.Task;

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                CancellationToken.None
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            finish.SetResult();
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_renews_for_the_configured_lease_duration() =>
            _harness.Leases.LastLeaseSeconds.Should().Be(300);

        [Test]
        public void It_completes_after_a_successful_renewal()
        {
            _result.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _harness.Leases.Calls.Should().Equal("Renew", "Complete");
        }
    }

    [TestFixture("ownership lost", "RenewalOwnershipLost")]
    [TestFixture("failure unknown", "RenewalFailureUnknown")]
    [TestFixture("result unknown", "RenewalResultUnknown")]
    [TestFixture("exception", "RenewalException")]
    [TestFixture("synchronous exception", "RenewalException")]
    [TestFixture("timeout", "RenewalTimeout")]
    public class Given_a_renewal_that_does_not_succeed(string failure, string expectedReason)
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;
        private bool _handlerCancelled;

        [SetUp]
        public async Task Setup()
        {
            _handlerCancelled = false;
            _harness = new ExecutorHarness();
            _harness.Script.Run = async (_, _, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    _handlerCancelled = true;
                    throw;
                }
            };
            _harness.Leases.NextRenewal = failure switch
            {
                "ownership lost" => () => Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost()),
                "failure unknown" => () => Task.FromResult<JobWriteResult>(FailureUnknown()),
                "result unknown" => () => Task.FromResult<JobWriteResult>(ResultUnknown()),
                "exception" => () => Task.FromException<JobWriteResult>(new InvalidOperationException()),
                "synchronous exception" => () => throw new InvalidOperationException(),
                _ => () => new TaskCompletionSource<JobWriteResult>().Task,
            };

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                CancellationToken.None
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            if (failure == "timeout")
            {
                _harness.Time.Advance(_harness.Settings.RenewalTimeout);
            }
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_marks_uncertain_and_cancels_on_renewal_failure()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, expectedReason));
            _handlerCancelled.Should().BeTrue();
        }

        [Test]
        public void It_writes_no_outcome() => _harness.Leases.OutcomeWrites.Should().BeEmpty();

        [Test]
        public void It_logs_the_uncertain_exit_with_its_reason() =>
            _harness
                .Logger.Entries.Last()
                .Should()
                .Match<LogEntry>(entry =>
                    entry.EventId.Name == "OwnershipUncertainExit"
                    && (string?)entry.Field("Reason") == expectedReason
                );
    }

    [TestFixture]
    public class Given_a_renewal_failure_between_two_fences
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;
        private Exception? _secondFence;

        [SetUp]
        public async Task Setup()
        {
            _secondFence = null;
            _harness = new ExecutorHarness();
            TaskCompletionSource firstFenceDone = ScriptedLeaseRepository.NewSignal();
            TaskCompletionSource cancelled = ScriptedLeaseRepository.NewSignal();
            TaskCompletionSource secondFence = ScriptedLeaseRepository.NewSignal();
            _harness.Leases.NextRenewal = () =>
                Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost());
            _harness.Script.Run = async (context, _, token) =>
            {
                token.Register(() => cancelled.TrySetResult());
                await context.Fence.ExecuteAsync((_, _) => Task.CompletedTask, CancellationToken.None);
                firstFenceDone.SetResult();
                await secondFence.Task;
                try
                {
                    await context.Fence.ExecuteAsync((_, _) => Task.CompletedTask, CancellationToken.None);
                }
                catch (JobLeaseLostException lost)
                {
                    _secondFence = lost;
                    throw;
                }
            };

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                CancellationToken.None
            );
            await firstFenceDone.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            await cancelled.Task.WaitAsync(_wait);
            secondFence.SetResult();
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_suppresses_fence_and_completion_after_renewal_failure()
        {
            _secondFence.Should().BeOfType<JobLeaseLostException>();
            _harness.Fences.DatabaseCalls.Should().Be(1, "the second fence stopped before the database");
            _harness.Leases.OutcomeWrites.Should().BeEmpty();
        }

        [Test]
        public void It_keeps_the_renewal_failure_as_the_reason() =>
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "RenewalOwnershipLost"));
    }

    [TestFixture]
    public class Given_a_cancellation_callback_that_waits_for_a_fence
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;
        private Exception? _callbackFence;

        [SetUp]
        public async Task Setup()
        {
            _callbackFence = null;
            _harness = new ExecutorHarness();
            _harness.Leases.NextRenewal = () =>
                Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost());
            _harness.Script.Run = async (context, _, token) =>
            {
                // A consumer's callback that blocks until a fence with its own token finishes. Were the renewal
                // loop to run callbacks while it holds the gate, this fence could never enter it.
                using CancellationTokenRegistration registration = token.Register(() =>
                {
                    try
                    {
                        context
                            .Fence.ExecuteAsync((_, _) => Task.CompletedTask, CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception exception)
                    {
                        _callbackFence = exception;
                    }
                });
                await Task.Delay(Timeout.Infinite, token);
            };

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                CancellationToken.None
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_finishes_the_execution() =>
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "RenewalOwnershipLost"));

        [Test]
        public void It_rejects_the_callback_fence_before_the_database()
        {
            _callbackFence.Should().BeOfType<JobLeaseLostException>();
            _harness.Fences.DatabaseCalls.Should().Be(0);
            _harness.Leases.OutcomeWrites.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_renewal_that_fails_while_the_execution_finalizes
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            TaskCompletionSource<JobWriteResult> renewal = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            TaskCompletionSource finish = ScriptedLeaseRepository.NewSignal();
            TaskCompletionSource finished = ScriptedLeaseRepository.NewSignal();
            _harness.Leases.NextRenewal = () => renewal.Task;
            _harness.Script.Run = async (_, _, _) =>
            {
                await finish.Task;
                finished.SetResult();
            };

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                CancellationToken.None
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            finish.SetResult();
            await finished.Task.WaitAsync(_wait);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            running.IsCompleted.Should().BeFalse("finalization waits for the in-flight renewal");
            renewal.SetResult(new JobWriteResult.OwnershipLost());
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_suppresses_completion_when_in_flight_renewal_fails_during_finalization()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "RenewalOwnershipLost"));
            _harness.Leases.OutcomeWrites.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_renewal_that_fails_during_shutdown
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            TaskCompletionSource<JobWriteResult> renewal = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _harness.Leases.NextRenewal = () => renewal.Task;
            _harness.Script.Run = (_, _, token) => Task.Delay(Timeout.Infinite, token);
            using CancellationTokenSource stopping = new();

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                stopping.Token
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            await stopping.CancelAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            renewal.SetResult(FailureUnknown());
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_suppresses_release_when_renewal_fails_during_shutdown()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "RenewalFailureUnknown"));
            _harness.Leases.OutcomeWrites.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_fence_waiting_on_a_pending_renewal
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;
        private int _fenceCallsWhileRenewalPending;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            TaskCompletionSource<JobWriteResult> renewal = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            TaskCompletionSource callFence = ScriptedLeaseRepository.NewSignal();
            _harness.Leases.NextRenewal = () => renewal.Task;
            _harness.Script.Run = async (context, _, token) =>
            {
                await callFence.Task;
                await context.Fence.ExecuteAsync((_, _) => Task.CompletedTask, token);
            };

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                CancellationToken.None
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await _harness.TriggerRenewalAsync();
            callFence.SetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            _fenceCallsWhileRenewalPending = _harness.Fences.DatabaseCalls;
            renewal.SetResult(new JobWriteResult.Success(null, DateTime.UtcNow));
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_holds_the_fence_behind_the_renewal() => _fenceCallsWhileRenewalPending.Should().Be(0);

        [Test]
        public void It_runs_the_fence_and_completes_once_the_renewal_finishes()
        {
            _harness.Fences.DatabaseCalls.Should().Be(1);
            _result.Outcome.Should().Be(JobExecutionOutcome.Completed);
        }
    }

    [TestFixture]
    public class Given_the_host_stopping_while_the_execution_owns_the_job
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, token) => Task.Delay(Timeout.Infinite, token);
            using CancellationTokenSource stopping = new();

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(),
                stopping.Token
            );
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await stopping.CancelAsync();
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_releases_the_job_to_pending_with_its_attempt_kept()
        {
            _result.Should().Be(new JobExecutionResult(JobExecutionOutcome.ReleasedOnShutdown));
            _harness.Leases.OutcomeWrites.Should().Equal("ReleaseToPending");
        }
    }

    [TestFixture(1, "FailTransient:30", "RetryScheduled")]
    [TestFixture(2, "FailTransient:60", "RetryScheduled")]
    [TestFixture(
        5,
        "FailTerminal:AttemptsExhausted:The job exceeded the maximum number of attempts.",
        "Failed"
    )]
    public class Given_a_handler_that_fails(int attempt, string expectedWrite, string expectedOutcome)
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) => throw new InvalidOperationException("The handler failed.");
            _result = await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(attempt, attempt),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_retries_with_backoff_or_marks_error_when_attempts_exhausted()
        {
            _result.Outcome.Should().Be(Enum.Parse<JobExecutionOutcome>(expectedOutcome));
            _harness.Leases.OutcomeWrites.Should().Equal(expectedWrite);
        }
    }

    [TestFixture("ConsumerRejected", "FailTerminal:ConsumerRejected:The consumer rejected the job.")]
    [TestFixture("NotRegistered", "FailTerminal:HandlerFailed:The job handler failed.")]
    public class Given_a_handler_that_fails_permanently(string code, string expectedWrite)
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) =>
                throw new JobPermanentException(
                    new JobErrorCode(code, "Text a handler chose, never published.")
                );
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_terminally_with_the_registered_message_only()
        {
            _result.Outcome.Should().Be(JobExecutionOutcome.Failed);
            _harness.Leases.OutcomeWrites.Should().Equal(expectedWrite);
        }
    }

    [TestFixture("unknown type", "Test.Unknown", 1, """{"code":"a","number":1}""", "UnsupportedJobType")]
    [TestFixture(
        "unsupported version",
        ExecutorHarness.JobType,
        3,
        """{"code":"a","number":1}""",
        "UnsupportedPayloadVersion"
    )]
    [TestFixture(
        "unexpected member",
        ExecutorHarness.JobType,
        1,
        """{"code":"a","number":1,"extra":2}""",
        "InvalidPayload"
    )]
    [TestFixture(
        "validator failure",
        ExecutorHarness.JobType,
        1,
        """{"code":"a","number":-1}""",
        "InvalidPayload"
    )]
    public class Given_a_job_the_executor_cannot_run(
        string label,
        string jobType,
        int payloadVersion,
        string payloadJson,
        string expectedCode
    )
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _result = await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(
                    jobType: jobType,
                    payloadVersion: (short)payloadVersion,
                    payloadJson: payloadJson
                ),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_terminally_before_handler_resolution()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: expectedCode), label);
            _harness.Script.Resolutions.Should().Be(0);
            _harness
                .Leases.OutcomeWrites.Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"FailTerminal:{expectedCode}:");
        }

        [Test]
        public void It_disposes_the_scope() => _harness.Scopes.Disposed.Should().Be(_harness.Scopes.Created);
    }

    [TestFixture]
    public class Given_a_stored_payload_its_constructor_rejects
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _result = await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(
                    jobType: ExecutorHarness.RangeCheckedJobType,
                    payloadJson: """{"dataStoreId":-1}"""
                ),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_terminally_with_the_registered_invalid_payload_message()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: "InvalidPayload"));
            _harness
                .Leases.OutcomeWrites.Should()
                .Equal($"FailTerminal:InvalidPayload:{JobErrorCode.InvalidPayload.Message}");
        }

        [Test]
        public void It_never_resolves_the_handler() => _harness.Script.Resolutions.Should().Be(0);

        [Test]
        public void It_logs_nothing_of_the_constructor_exception() =>
            _harness
                .Logger.Entries.Should()
                .NotContain(entry =>
                    entry.Message.Contains("dataStoreId") || entry.Message.Contains("ArgumentOutOfRange")
                );
    }

    [TestFixture]
    public class Given_a_tenant_job_in_a_multi_tenant_service
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness(multiTenancy: true);
            _harness.Tenants.Tenants[7] = "district-7";
            _result = await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(tenantId: 7),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_installs_the_tenant_before_the_handler_is_resolved()
        {
            _harness.Script.TenantAtResolution.Should().Be(new TenantContext.Multitenant(7, "district-7"));
            _result.Outcome.Should().Be(JobExecutionOutcome.Completed);
        }
    }

    [TestFixture("missing tenant", true, 8L, "FailTerminal:TenantUnavailable:")]
    [TestFixture("tenant job, single-tenant service", false, 7L, "FailTerminal:TenantUnavailable:")]
    [TestFixture("single-tenant job, multi-tenant service", true, null, "FailTerminal:TenantUnavailable:")]
    public class Given_a_tenant_that_cannot_be_installed(
        string label,
        bool multiTenancy,
        long? tenantId,
        string expectedWrite
    )
    {
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness(multiTenancy);
            _harness.Tenants.Tenants[7] = "district-7";
            await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(tenantId: tenantId),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_with_tenant_unavailable_without_resolving_the_handler()
        {
            _harness.Script.Resolutions.Should().Be(0, label);
            _harness.Leases.OutcomeWrites.Should().ContainSingle().Which.Should().StartWith(expectedWrite);
        }

        [Test]
        public void It_disposes_the_scope() => _harness.Scopes.Disposed.Should().Be(1);
    }

    [TestFixture]
    public class Given_a_tenant_lookup_that_fails
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness(multiTenancy: true);
            _harness.Tenants.Fails = true;
            _result = await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(tenantId: 7),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_retries_the_job_rather_than_failing_it()
        {
            _result.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
            _harness.Script.Resolutions.Should().Be(0);
        }
    }

    /// <summary>
    /// Settings that pass startup validation with no slack (12 + 5 + 1 + 5 + 6 + 10 = 39 s), and a lease the scripted
    /// repository models by time: a renewal succeeds only before the current expiry and then extends it.
    /// </summary>
    private static void UseTightLease(ExecutorHarness harness, Action onRenewalRejected)
    {
        harness.Settings.LeaseDuration = TimeSpan.FromSeconds(39);
        harness.Settings.RenewalInterval = TimeSpan.FromSeconds(12);
        harness.Settings.FenceTimeout = TimeSpan.FromSeconds(1);
        DateTimeOffset leaseExpiresAt = harness.Time.GetUtcNow() + harness.Settings.LeaseDuration;
        harness.Leases.NextRenewal = () =>
        {
            DateTimeOffset now = harness.Time.GetUtcNow();
            if (now >= leaseExpiresAt)
            {
                onRenewalRejected();
                return Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost());
            }

            leaseExpiresAt = now + harness.Settings.LeaseDuration;
            return ScriptedLeaseRepository.Succeed();
        };
    }

    /// <summary>Moves the fake clock one second at a time, letting the renewal loop run after each step.</summary>
    private static async Task AdvanceSecondsAsync(ExecutorHarness harness, int seconds)
    {
        for (int second = 0; second < seconds; second++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    [TestFixture]
    public class Given_a_tenant_lookup_that_outlasts_a_renewal_interval
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;
        private int _renewalsDuringLookup;
        private int _rejectedRenewals;

        [SetUp]
        public async Task Setup()
        {
            _rejectedRenewals = 0;
            _harness = new ExecutorHarness(multiTenancy: true);
            _harness.Tenants.Tenants[7] = "district-7";
            UseTightLease(_harness, () => _rejectedRenewals++);

            TaskCompletionSource lookupStarted = ScriptedLeaseRepository.NewSignal();
            TaskCompletionSource lookup = ScriptedLeaseRepository.NewSignal();
            _harness.Tenants.BeforeLookup = () =>
            {
                lookupStarted.TrySetResult();
                return lookup.Task;
            };
            TaskCompletionSource finish = ScriptedLeaseRepository.NewSignal();
            _harness.Script.Run = (_, _, token) => finish.Task.WaitAsync(token);

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(tenantId: 7),
                CancellationToken.None
            );
            await lookupStarted.Task.WaitAsync(_wait);

            // The lookup takes 28 s; the handler then runs until 45 s, past the claim's 39 s lease.
            await AdvanceSecondsAsync(_harness, 28);
            _renewalsDuringLookup = _harness.Leases.Calls.Count(call => call == "Renew");
            lookup.SetResult();
            await _harness.Script.Started.Task.WaitAsync(_wait);
            await AdvanceSecondsAsync(_harness, 17);
            finish.TrySetResult();
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_uses_settings_that_pass_startup_validation() =>
            new JobOptionsValidator().Validate(null, _harness.Settings).Succeeded.Should().BeTrue();

        [Test]
        public void It_renews_while_the_tenant_lookup_is_pending() => _renewalsDuringLookup.Should().Be(2);

        [Test]
        public void It_keeps_the_lease_and_completes_the_job()
        {
            _rejectedRenewals.Should().Be(0);
            _result.Should().Be(new JobExecutionResult(JobExecutionOutcome.Completed));
            _harness.Leases.Calls.Should().Equal("Renew", "Renew", "Renew", "Complete");
        }

        [Test]
        public void It_installs_the_tenant_before_the_handler_is_resolved() =>
            _harness.Script.TenantAtResolution.Should().Be(new TenantContext.Multitenant(7, "district-7"));
    }

    [TestFixture]
    public class Given_a_renewal_that_fails_while_the_tenant_lookup_is_pending
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness(multiTenancy: true);
            _harness.Tenants.Tenants[7] = "district-7";
            UseTightLease(_harness, () => { });
            _harness.Leases.NextRenewal = () =>
                Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost());

            TaskCompletionSource lookupStarted = ScriptedLeaseRepository.NewSignal();
            TaskCompletionSource lookup = ScriptedLeaseRepository.NewSignal();
            _harness.Tenants.BeforeLookup = () =>
            {
                lookupStarted.TrySetResult();
                return lookup.Task;
            };

            Task<JobExecutionResult> running = _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(tenantId: 7),
                CancellationToken.None
            );
            await lookupStarted.Task.WaitAsync(_wait);
            await AdvanceSecondsAsync(_harness, 13);
            lookup.SetResult();
            _result = await running.WaitAsync(_wait);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_never_resolves_the_handler() => _harness.Script.Resolutions.Should().Be(0);

        [Test]
        public void It_ends_uncertain_without_an_outcome_write()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "RenewalOwnershipLost"));
            _harness.Leases.OutcomeWrites.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_preparation_that_throws
    {
        private ExecutorHarness _harness = null!;
        private Exception? _failure;
        private int _renewalsAfterwards;

        [SetUp]
        public async Task Setup()
        {
            _failure = null;
            _harness = new ExecutorHarness();
            try
            {
                await _harness.Executor.ExecuteAsync(
                    ExecutorHarness.Job(
                        payloadJson: $$"""{"code":"a","number":{{ExecutorValidator.Throws}}}"""
                    ),
                    CancellationToken.None
                );
            }
            catch (Exception exception)
            {
                _failure = exception;
            }

            await AdvanceSecondsAsync(_harness, (int)_harness.Settings.RenewalInterval.TotalSeconds * 2);
            _renewalsAfterwards = _harness.Leases.Calls.Count(call => call == "Renew");
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_lets_the_programming_error_escape() =>
            _failure.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_stops_the_renewal_loop_so_no_renewal_outlives_the_execution() =>
            _renewalsAfterwards.Should().Be(0);

        [Test]
        public void It_disposes_the_scope() => _harness.Scopes.Disposed.Should().Be(1);
    }

    [TestFixture]
    public class Given_a_completion_whose_result_is_unknown_after_it_committed
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;
        private JobClaimResult _laterClaim = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Leases.CompletionCommits = true;
            _harness.Leases.OutcomeResult = _ => Task.FromResult<JobWriteResult>(ResultUnknown());
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
            _laterClaim = await _harness.Leases.ClaimNext(
                "worker-b",
                300,
                ExecutorHarness.MaxAttempts,
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_marks_uncertain_without_retrying_or_rereading()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "WriteOutcomeUnknown"));
            _harness.Leases.Calls.Should().Equal("Complete", "ClaimNext");
        }

        [Test]
        public void It_leaves_committed_completion_terminal_when_acknowledgement_is_lost() =>
            _laterClaim.Should().BeOfType<JobClaimResult.NoneAvailable>();
    }

    [TestFixture]
    public class Given_a_completion_whose_result_is_unknown_after_it_rolled_back
    {
        private ExecutorHarness _harness = null!;
        private JobClaimResult _laterClaim = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Leases.OutcomeResult = _ => Task.FromResult<JobWriteResult>(ResultUnknown());
            await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
            _harness.Leases.Row.LeaseExpired = true;
            _laterClaim = await _harness.Leases.ClaimNext(
                "worker-b",
                300,
                ExecutorHarness.MaxAttempts,
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_leaves_in_progress_for_reclaim_when_outcome_rolled_back() =>
            _laterClaim
                .Should()
                .BeOfType<JobClaimResult.Claimed>()
                .Which.Job.Should()
                .Match<ClaimedJob>(job => job.AttemptCount == 2 && job.FencingToken == 2 && job.Reclaimed);
    }

    [TestFixture("ownership lost", "LateWriteRejected", "LateWriteRejected")]
    [TestFixture("failure unknown", "OwnershipUncertain", "WriteFailed")]
    [TestFixture("exception", "OwnershipUncertain", "WriteOutcomeUnknown")]
    public class Given_an_outcome_write_that_does_not_succeed(
        string failure,
        string expectedOutcome,
        string reason
    )
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Leases.OutcomeResult = failure switch
            {
                "ownership lost" => _ => Task.FromResult<JobWriteResult>(new JobWriteResult.OwnershipLost()),
                "failure unknown" => _ => Task.FromResult<JobWriteResult>(FailureUnknown()),
                _ => _ => Task.FromException<JobWriteResult>(new InvalidOperationException()),
            };
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_records_the_outcome_and_never_writes_again()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(Enum.Parse<JobExecutionOutcome>(expectedOutcome), reason));
            _harness.Leases.OutcomeWrites.Should().Equal("Complete");
        }
    }

    [TestFixture]
    public class Given_a_fence_that_finds_the_lease_lost
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Fences.LeaseLost = true;
            _harness.Script.Run = (context, _, token) =>
                context.Fence.ExecuteAsync((_, _) => Task.CompletedTask, token);
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_ends_uncertain_without_an_outcome_write()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "FenceLeaseLost"));
            _harness.Leases.OutcomeWrites.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_failure_whose_message_and_payload_carry_secrets
    {
        private const string SecretMessage = "Server=db;Password=hunter2";
        private const string PayloadMarker = "payload-marker-4711";
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) =>
                throw new InvalidOperationException(SecretMessage, new FormatException(SecretMessage));
            await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(
                    payloadJson: $$"""{"code":"{{PayloadMarker}}","number":1}""",
                    jobId: "job-11\nFORGED log line"
                ),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_logs_outcome_fields_without_payload_or_messages()
        {
            foreach (LogEntry entry in _harness.Logger.Entries)
            {
                entry.Message.Should().NotContain("hunter2").And.NotContain(PayloadMarker);
                entry
                    .Fields.Select(field => field.Value?.ToString() ?? "")
                    .Should()
                    .NotContain(value => value.Contains("hunter2") || value.Contains(PayloadMarker));
            }
        }

        [Test]
        public void It_logs_the_retry_with_the_type_chain_and_the_outcome_fields()
        {
            LogEntry retry = _harness.Logger.Entries.Single(entry =>
                entry.EventId.Name == "JobRetryScheduled"
            );
            retry.Level.Should().Be(LogLevel.Warning);
            retry
                .Field("ExceptionTypeChain")
                .Should()
                .Be("System.InvalidOperationException/System.FormatException");
            retry.Field("Operation").Should().Be("Handler");
            retry.Field("Attempt").Should().Be(1);
            retry.Field("Outcome").Should().Be(JobExecutionOutcome.RetryScheduled);
            retry.Field("DurationMs").Should().BeOfType<double>();
        }

        [Test]
        public void It_logs_identifiers_without_control_characters() =>
            _harness
                .Logger.Entries.Select(entry => (string?)entry.Field("JobId"))
                .Should()
                .OnlyContain(jobId => jobId == "job-11FORGED log line");
    }

    [TestFixture]
    public class Given_a_reclaimed_job
    {
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            await _harness.Executor.ExecuteAsync(
                ExecutorHarness.Job(2, 2, reclaimed: true),
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_logs_the_reclaim() =>
            _harness.Logger.Entries.First().EventId.Name.Should().Be("JobReclaimed");
    }
}
