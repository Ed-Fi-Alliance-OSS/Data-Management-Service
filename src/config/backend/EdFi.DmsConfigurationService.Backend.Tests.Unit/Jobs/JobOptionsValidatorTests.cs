// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(JobOptions options) =>
        new JobOptionsValidator().Validate(null, options);

    [TestFixture]
    public class Given_the_default_options
    {
        private readonly JobOptions _options = new();
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() => _result = Validate(_options);

        [Test]
        public void It_accepts_them() => _result.Succeeded.Should().BeTrue();

        [Test]
        public void It_uses_the_approved_candidates()
        {
            _options.WorkerEnabled.Should().BeTrue();
            _options.SchedulerEnabled.Should().BeTrue();
            _options.RetentionEnabled.Should().BeTrue();
            _options.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
            _options.LeaseDuration.Should().Be(TimeSpan.FromMinutes(5));
            _options.RenewalInterval.Should().Be(TimeSpan.FromMinutes(1));
            _options.FenceTimeout.Should().Be(TimeSpan.FromSeconds(10));
            _options.MaxAttempts.Should().Be(5);
            _options.RetryBackoffBase.Should().Be(TimeSpan.FromSeconds(30));
            _options.RetryBackoffMaximum.Should().Be(TimeSpan.FromMinutes(15));
            _options.MaxConcurrentJobs.Should().Be(2);
            _options.FinishedJobRetention.Should().Be(TimeSpan.FromDays(7));
            _options.RetentionInterval.Should().Be(TimeSpan.FromHours(1));
            _options.RetentionBatchSize.Should().Be(500);
        }

        [Test]
        public void It_derives_the_renewal_timeout_as_half_the_renewal_interval() =>
            _options.RenewalTimeout.Should().Be(TimeSpan.FromSeconds(30));

        [Test]
        public void It_publishes_the_configured_lease_timings() =>
            _options
                .LeaseTimings.Should()
                .Be(new JobLeaseTimings(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)));
    }

    [TestFixture(
        "PollInterval",
        "00:00:00.9",
        "JobSettings:PollInterval must be between 00:00:01 and 00:05:00",
        1
    )]
    [TestFixture(
        "PollInterval",
        "00:05:01",
        "JobSettings:PollInterval must be between 00:00:01 and 00:05:00",
        1
    )]
    [TestFixture(
        "LeaseDuration",
        "00:00:29",
        "JobSettings:LeaseDuration must be between 00:00:30 and 01:00:00",
        3
    )]
    [TestFixture(
        "LeaseDuration",
        "01:00:01",
        "JobSettings:LeaseDuration must be between 00:00:30 and 01:00:00",
        1
    )]
    [TestFixture(
        "RenewalInterval",
        "00:00:11",
        "JobSettings:RenewalInterval must be between 00:00:12 and JobSettings:LeaseDuration / 3 (00:01:40)",
        1
    )]
    [TestFixture(
        "RenewalInterval",
        "00:01:41",
        "JobSettings:RenewalInterval must be between 00:00:12 and JobSettings:LeaseDuration / 3 (00:01:40)",
        1
    )]
    [TestFixture(
        "FenceTimeout",
        "00:00:00.9",
        "JobSettings:FenceTimeout must be between 00:00:01 and 00:01:00",
        1
    )]
    [TestFixture(
        "FenceTimeout",
        "00:01:01",
        "JobSettings:FenceTimeout must be between 00:00:01 and 00:01:00",
        1
    )]
    [TestFixture("MaxAttempts", "0", "JobSettings:MaxAttempts must be between 1 and 20; it is 0.", 1)]
    [TestFixture("MaxAttempts", "21", "JobSettings:MaxAttempts must be between 1 and 20; it is 21.", 1)]
    [TestFixture(
        "RetryBackoffBase",
        "00:00:00.5",
        "JobSettings:RetryBackoffBase must be between 00:00:01 and 01:00:00",
        1
    )]
    [TestFixture(
        "RetryBackoffBase",
        "01:00:01",
        "JobSettings:RetryBackoffBase must be between 00:00:01 and 01:00:00",
        2
    )]
    [TestFixture(
        "RetryBackoffMaximum",
        "00:00:29",
        "JobSettings:RetryBackoffMaximum must be at least JobSettings:RetryBackoffBase (00:00:30) and at most 1.00:00:00",
        1
    )]
    [TestFixture(
        "RetryBackoffMaximum",
        "1.00:00:01",
        "JobSettings:RetryBackoffMaximum must be at least JobSettings:RetryBackoffBase (00:00:30) and at most 1.00:00:00",
        1
    )]
    [TestFixture(
        "MaxConcurrentJobs",
        "0",
        "JobSettings:MaxConcurrentJobs must be between 1 and 32; it is 0.",
        1
    )]
    [TestFixture(
        "MaxConcurrentJobs",
        "33",
        "JobSettings:MaxConcurrentJobs must be between 1 and 32; it is 33.",
        1
    )]
    [TestFixture(
        "FinishedJobRetention",
        "00:59:59",
        "JobSettings:FinishedJobRetention must be between 01:00:00 and 365.00:00:00",
        1
    )]
    [TestFixture(
        "FinishedJobRetention",
        "365.00:00:01",
        "JobSettings:FinishedJobRetention must be between 01:00:00 and 365.00:00:00",
        1
    )]
    [TestFixture(
        "RetentionInterval",
        "00:00:59",
        "JobSettings:RetentionInterval must be between 00:01:00 and 1.00:00:00",
        1
    )]
    [TestFixture(
        "RetentionInterval",
        "1.00:00:01",
        "JobSettings:RetentionInterval must be between 00:01:00 and 1.00:00:00",
        1
    )]
    [TestFixture(
        "RetentionBatchSize",
        "0",
        "JobSettings:RetentionBatchSize must be between 1 and 2000; it is 0.",
        1
    )]
    [TestFixture(
        "RetentionBatchSize",
        "2001",
        "JobSettings:RetentionBatchSize must be between 1 and 2000; it is 2001.",
        1
    )]
    public class Given_one_setting_outside_its_bounds(
        string property,
        string value,
        string expectedMessage,
        int expectedFailures
    )
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() => _result = Validate(With(property, value));

        /// <summary>Sets one <see cref="JobOptions"/> property from its configuration text, as the binder would.</summary>
        private static JobOptions With(string property, string value)
        {
            JobOptions options = new();
            System.Reflection.PropertyInfo info = typeof(JobOptions).GetProperty(property)!;
            info.SetValue(
                options,
                info.PropertyType == typeof(TimeSpan)
                    ? TimeSpan.Parse(value, CultureInfo.InvariantCulture)
                    : int.Parse(value, CultureInfo.InvariantCulture)
            );
            return options;
        }

        [Test]
        public void It_rejects_the_options() => _result.Failed.Should().BeTrue();

        [Test]
        public void It_names_the_setting_its_range_and_its_value()
        {
            string failure = _result
                .Failures.Should()
                .ContainSingle(message => message.StartsWith(expectedMessage))
                .Subject;
            string shown =
                typeof(JobOptions).GetProperty(property)!.PropertyType == typeof(TimeSpan)
                    ? TimeSpan
                        .Parse(value, CultureInfo.InvariantCulture)
                        .ToString("c", CultureInfo.InvariantCulture)
                    : value;
            failure.Should().Contain($"it is {shown}");
        }

        [Test]
        public void It_reports_every_rule_the_setting_breaks() =>
            _result.Failures.Should().HaveCount(expectedFailures);
    }

    [TestFixture]
    public class Given_a_lease_too_short_for_a_late_renewal
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = Validate(
                new JobOptions
                {
                    LeaseDuration = TimeSpan.FromMinutes(1),
                    RenewalInterval = TimeSpan.FromSeconds(20),
                    FenceTimeout = TimeSpan.FromSeconds(30),
                }
            );

        [Test]
        public void It_rejects_only_the_lateness_inequality() =>
            _result
                .Failures.Should()
                .ContainSingle()
                .Which.Should()
                .StartWith("JobSettings:LeaseDuration (00:01:00) must be at least");

        [Test]
        public void It_reports_the_value_of_every_term() =>
            _result
                .Failures!.Single()
                .Should()
                .Contain("RenewalInterval (00:00:20)")
                .And.Contain("FenceLockWait (00:00:05)")
                .And.Contain("FenceTimeout (00:00:30)")
                .And.Contain("WriteLockWait (00:00:05)")
                .And.Contain("RenewalTimeout (00:00:10)")
                .And.Contain("SafetyMargin (00:00:10")
                .And.Contain("= 00:01:20");
    }

    [TestFixture]
    public class Given_a_lease_that_exactly_fits_a_late_renewal
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            // 20 + 5 + 10 + 5 + 10 + 10 = 60 s: equal to the lease, which the inequality allows.
            _result = Validate(
                new JobOptions
                {
                    LeaseDuration = TimeSpan.FromMinutes(1),
                    RenewalInterval = TimeSpan.FromSeconds(20),
                    FenceTimeout = TimeSpan.FromSeconds(10),
                }
            );

        [Test]
        public void It_accepts_the_options() => _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_leases_either_side_of_the_safety_margin_crossover
    {
        [Test]
        public void It_uses_ten_seconds_for_a_short_lease() =>
            JobOptionsValidator.SafetyMargin(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(10));

        [Test]
        public void It_uses_a_sixth_of_a_long_lease() =>
            JobOptionsValidator.SafetyMargin(TimeSpan.FromMinutes(5)).Should().Be(TimeSpan.FromSeconds(50));
    }

    [TestFixture("RenewalInterval", true)]
    [TestFixture("FenceTimeout", true)]
    [TestFixture("RenewalInterval", false)]
    [TestFixture("FenceTimeout", false)]
    public class Given_a_duration_at_the_limit_of_its_type(string property, bool maximum)
    {
        private Exception? _thrown;
        private ValidateOptionsResult? _result;

        [SetUp]
        public void Setup()
        {
            JobOptions options = new();
            typeof(JobOptions)
                .GetProperty(property)!
                .SetValue(options, maximum ? TimeSpan.MaxValue : TimeSpan.MinValue);
            try
            {
                _result = Validate(options);
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }
        }

        [Test]
        public void It_validates_without_overflowing() => _thrown.Should().BeNull();

        [Test]
        public void It_still_reports_the_setting_outside_its_range() =>
            _result!
                .Failures.Should()
                .Contain(message => message.StartsWith($"JobSettings:{property} must be between"));

        [Test]
        public void It_reports_a_lateness_total_beyond_the_duration_range_without_formatting_it()
        {
            if (maximum)
            {
                _result!
                    .Failures.Should()
                    .ContainSingle(message =>
                        message.StartsWith("JobSettings:LeaseDuration (00:05:00) must be at least")
                    )
                    .Which.Should()
                    .Contain("= more than 10675199.02:48:05.4775807");
            }
            else
            {
                _result!
                    .Failures.Should()
                    .NotContain(message => message.StartsWith("JobSettings:LeaseDuration"));
            }
        }
    }

    [TestFixture]
    public class Given_several_invalid_settings
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = Validate(new JobOptions { MaxAttempts = 0, RetentionBatchSize = 2_001 });

        [Test]
        public void It_reports_each_of_them() =>
            _result
                .Failures.Should()
                .BeEquivalentTo(
                    "JobSettings:MaxAttempts must be between 1 and 20; it is 0.",
                    "JobSettings:RetentionBatchSize must be between 1 and 2000; it is 2001."
                );
    }
}
