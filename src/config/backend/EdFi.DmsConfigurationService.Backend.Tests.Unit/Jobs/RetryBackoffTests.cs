// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class RetryBackoffTests
{
    private static readonly TimeSpan _base = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _maximum = TimeSpan.FromMinutes(15);

    [TestFixture]
    public class Given_the_candidate_base_and_maximum
    {
        private TimeSpan[] _delays = [];
        private int[] _seconds = [];

        [SetUp]
        public void Setup()
        {
            _delays =
            [
                .. Enumerable.Range(1, 8).Select(attempt => RetryBackoff.For(attempt, _base, _maximum)),
            ];
            _seconds =
            [
                .. Enumerable
                    .Range(1, 8)
                    .Select(attempt => RetryBackoff.SecondsFor(attempt, _base, _maximum)),
            ];
        }

        [Test]
        public void It_doubles_from_the_base_after_each_attempt_until_the_maximum() =>
            _delays
                .Should()
                .Equal(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(2),
                    TimeSpan.FromMinutes(4),
                    TimeSpan.FromMinutes(8),
                    TimeSpan.FromMinutes(15),
                    TimeSpan.FromMinutes(15),
                    TimeSpan.FromMinutes(15)
                );

        [Test]
        public void It_reports_the_same_delays_in_whole_seconds() =>
            _seconds.Should().Equal(30, 60, 120, 240, 480, 900, 900, 900);
    }

    [TestFixture]
    public class Given_an_attempt_count_far_beyond_the_cap
    {
        private TimeSpan _delay;

        [SetUp]
        public void Setup() => _delay = RetryBackoff.For(int.MaxValue, _base, _maximum);

        [Test]
        public void It_returns_the_maximum_without_overflowing() => _delay.Should().Be(_maximum);
    }

    [TestFixture]
    public class Given_a_fractional_second_base
    {
        private int _seconds;

        [SetUp]
        public void Setup() =>
            _seconds = RetryBackoff.SecondsFor(1, TimeSpan.FromMilliseconds(1_500), TimeSpan.FromHours(1));

        [Test]
        public void It_rounds_the_delay_up_to_a_whole_second() => _seconds.Should().Be(2);
    }

    [TestFixture]
    public class Given_invalid_arguments
    {
        [Test]
        public void It_rejects_an_attempt_count_below_one() =>
            FluentActions
                .Invoking(() => RetryBackoff.For(0, _base, _maximum))
                .Should()
                .Throw<ArgumentOutOfRangeException>();

        [Test]
        public void It_rejects_a_maximum_below_the_base() =>
            FluentActions
                .Invoking(() => RetryBackoff.For(1, _maximum, _base))
                .Should()
                .Throw<ArgumentOutOfRangeException>();
    }
}
