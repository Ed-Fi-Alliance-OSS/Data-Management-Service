// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobExecutorCompositeErrorTests
{
    private const string Forged = "FORGED-text-4711";
    private const string HandlerFailedWrite = "FailTerminal:HandlerFailed:The job handler failed.";

    private static JobErrorCode Code(string code) => new(code, $"{Forged} for {code}");

    [TestFixture]
    public class Given_a_handler_that_fails_with_a_composite_error
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) =>
                throw new JobPermanentException(
                    Code("ConsumerIncomplete"),
                    [Code("ConsumerBusy"), Code("ConsumerRejected"), Code("ConsumerBusy")]
                );
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_records_the_composite_code_with_registered_text_only()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: "ConsumerIncomplete"));
            _harness
                .Leases.OutcomeWrites.Should()
                .Equal(
                    "FailTerminal:ConsumerIncomplete:The consumer could not finish every part of the job. "
                        + "Problem categories: ConsumerBusy, ConsumerRejected. "
                        + "The consumer was busy. The consumer rejected the job."
                );
        }

        [Test]
        public void It_logs_no_rejection_and_no_caller_text()
        {
            _harness
                .Logger.Entries.Should()
                .NotContain(entry => entry.EventId.Name == "JobCompositeErrorRejected");
            foreach (LogEntry entry in _harness.Logger.Entries)
            {
                entry.Message.Should().NotContain(Forged);
            }
        }
    }

    [TestFixture("UnregisteredPrimary", new[] { "ConsumerBusy" })]
    [TestFixture("ConsumerRejected", new[] { "ConsumerBusy" })]
    [TestFixture("ConsumerIncomplete", new string[0])]
    [TestFixture("ConsumerIncomplete", new[] { "ConsumerBusy", "NotRegistered" })]
    [TestFixture("ConsumerIncomplete", new[] { "ConsumerBusy", "UnsupportedJobType" })]
    public class Given_a_composite_error_that_cannot_be_composed(string primary, string[] categories)
    {
        private ExecutorHarness _harness = null!;
        private JobExecutionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) =>
                throw new JobPermanentException(Code(primary), categories.Select(Code).ToArray());
            _result = await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_closed_to_handler_failed()
        {
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: "HandlerFailed"));
            _harness.Leases.OutcomeWrites.Should().Equal(HandlerFailedWrite);
        }

        [Test]
        public void It_logs_the_rejected_codes()
        {
            LogEntry rejected = _harness.Logger.Entries.Single(entry =>
                entry.EventId.Name == "JobCompositeErrorRejected"
            );
            rejected.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
            ((string?)rejected.Field("JobErrorCode")).Should().Be(primary);
            ((int)rejected.Field("CategoryCount")!).Should().Be(categories.Length);
            ((string?)rejected.Field("CategoryCodes")).Should().Be(string.Join(',', categories));
            rejected.Message.Should().NotContain(Forged);
        }
    }

    [TestFixture]
    public class Given_a_composite_error_with_missing_categories
    {
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) =>
                throw new JobPermanentException(Code("ConsumerIncomplete"), [Code("ConsumerBusy"), null!]);
            await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_closed_to_handler_failed() =>
            _harness.Leases.OutcomeWrites.Should().Equal(HandlerFailedWrite);
    }

    [TestFixture]
    public class Given_a_composite_error_code_whose_names_are_not_log_safe
    {
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) =>
                throw new JobPermanentException(
                    Code("Forged\nJobFailed line"),
                    [Code("ConsumerBusy"), Code("Bad,{code}\r\n")]
                );
            await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_logs_the_codes_sanitized()
        {
            LogEntry rejected = _harness.Logger.Entries.Single(entry =>
                entry.EventId.Name == "JobCompositeErrorRejected"
            );
            ((string?)rejected.Field("JobErrorCode")).Should().Be("ForgedJobFailed line");
            ((string?)rejected.Field("CategoryCodes")).Should().Be("ConsumerBusy,Badcode");
        }
    }

    [TestFixture]
    public class Given_a_composite_code_raised_without_categories
    {
        private ExecutorHarness _harness = null!;

        [SetUp]
        public async Task Setup()
        {
            _harness = new ExecutorHarness();
            _harness.Script.Run = (_, _, _) => throw new JobPermanentException(Code("ConsumerIncomplete"));
            await _harness.Executor.ExecuteAsync(ExecutorHarness.Job(), CancellationToken.None);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_fails_closed_as_an_unregistered_single_code() =>
            _harness.Leases.OutcomeWrites.Should().Equal(HandlerFailedWrite);
    }
}
