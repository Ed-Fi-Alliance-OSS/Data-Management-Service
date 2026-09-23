// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class ScheduleOccurrenceMathTests
{
    private static readonly DateTime _day = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(string timeOfDay) =>
        _day + TimeSpan.ParseExact(timeOfDay, @"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture);

    [TestFixture]
    public class Given_schedules_that_missed_whole_and_partial_intervals
    {
        private static readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
        private static readonly DateTime _nextRunAt = At("12:00:00.000");

        private readonly Dictionary<string, DateTime> _advanced = [];

        [SetUp]
        public void Setup()
        {
            _advanced["none"] = ScheduleOccurrenceMath.Advance(
                _nextRunAt,
                _interval,
                _nextRunAt.AddMinutes(2)
            );
            _advanced["one"] = ScheduleOccurrenceMath.Advance(
                _nextRunAt,
                _interval,
                _nextRunAt.AddMinutes(7)
            );
            _advanced["three_and_a_half"] = ScheduleOccurrenceMath.Advance(
                _nextRunAt,
                _interval,
                _nextRunAt.AddMinutes(17.5)
            );
            _advanced["one_hundred"] = ScheduleOccurrenceMath.Advance(
                _nextRunAt,
                _interval,
                _nextRunAt.AddMinutes(502)
            );
            _advanced["exact_boundary"] = ScheduleOccurrenceMath.Advance(
                _nextRunAt,
                _interval,
                _nextRunAt.AddMinutes(10)
            );
            _advanced["due_exactly_now"] = ScheduleOccurrenceMath.Advance(_nextRunAt, _interval, _nextRunAt);
            _advanced["not_yet_due"] = ScheduleOccurrenceMath.Advance(
                _nextRunAt,
                _interval,
                _nextRunAt.AddSeconds(-1)
            );
        }

        [Test]
        public void It_advances_to_the_first_boundary_after_now()
        {
            _advanced["none"].Should().Be(_nextRunAt.AddMinutes(5));
            _advanced["one"].Should().Be(_nextRunAt.AddMinutes(10));
            _advanced["three_and_a_half"].Should().Be(_nextRunAt.AddMinutes(20));
            _advanced["one_hundred"].Should().Be(_nextRunAt.AddMinutes(505));
        }

        [Test]
        public void It_moves_past_a_boundary_that_equals_now()
        {
            _advanced["exact_boundary"].Should().Be(_nextRunAt.AddMinutes(15));
            _advanced["due_exactly_now"].Should().Be(_nextRunAt.AddMinutes(5));
        }

        [Test]
        public void It_leaves_a_schedule_that_is_not_yet_due_unchanged() =>
            _advanced["not_yet_due"].Should().Be(_nextRunAt);
    }

    [TestFixture]
    public class Given_the_D9_fractional_second_vectors
    {
        private static readonly (
            string Name,
            int IntervalMinutes,
            DateTime NextRunAt,
            DateTime Now,
            DateTime Expected
        )[] _vectors =
        [
            ("worked_example", 1, At("12:00:00.900"), At("12:01:00.100"), At("12:01:00.900")),
            ("before_boundary_100ms", 1, At("12:00:00.000"), At("12:00:59.900"), At("12:01:00.000")),
            ("at_boundary", 1, At("12:00:00.000"), At("12:01:00.000"), At("12:02:00.000")),
            ("after_boundary_100ms", 1, At("12:00:00.000"), At("12:01:00.100"), At("12:02:00.000")),
            (
                "fractional_before_boundary_100ms",
                1,
                At("12:00:00.900"),
                At("12:01:00.800"),
                At("12:01:00.900")
            ),
            ("fractional_at_boundary", 1, At("12:00:00.900"), At("12:01:00.900"), At("12:02:00.900")),
            (
                "fractional_after_boundary_100ms",
                1,
                At("12:00:00.900"),
                At("12:01:01.000"),
                At("12:02:00.900")
            ),
            ("second_count_below_elapsed", 1, At("12:00:00.900"), At("12:00:59.950"), At("12:01:00.900")),
            ("second_count_above_elapsed", 1, At("12:00:00.100"), At("12:01:00.050"), At("12:01:00.100")),
            (
                "tick_before_boundary",
                1,
                At("12:00:00.9999999"),
                At("12:01:00.9999998"),
                At("12:01:00.9999999")
            ),
            (
                "downtime_663_intervals",
                5,
                At("00:00:00.250"),
                new DateTime(2026, 1, 3, 7, 15, 42, 125, DateTimeKind.Utc),
                new DateTime(2026, 1, 3, 7, 20, 0, 250, DateTimeKind.Utc)
            ),
            (
                "maximum_interval",
                527_040,
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 1, 3, 0, 0, 0, DateTimeKind.Utc)
            ),
        ];

        private readonly Dictionary<string, DateTime> _advanced = [];

        [SetUp]
        public void Setup()
        {
            foreach (
                (string name, int intervalMinutes, DateTime nextRunAt, DateTime now, DateTime _) in _vectors
            )
            {
                _advanced[name] = ScheduleOccurrenceMath.Advance(
                    nextRunAt,
                    TimeSpan.FromMinutes(intervalMinutes),
                    now
                );
            }
        }

        [Test]
        public void It_matches_every_expected_boundary()
        {
            foreach ((string name, int _, DateTime _, DateTime _, DateTime expected) in _vectors)
            {
                _advanced[name].Should().Be(expected, name);
            }
        }

        [Test]
        public void It_lands_strictly_after_now_and_at_most_one_interval_after_it()
        {
            foreach ((string name, int intervalMinutes, DateTime _, DateTime now, DateTime _) in _vectors)
            {
                _advanced[name].Should().BeAfter(now, name);
                (_advanced[name] - TimeSpan.FromMinutes(intervalMinutes)).Should().BeOnOrBefore(now, name);
            }
        }

        [Test]
        public void It_keeps_the_kind_of_the_input() =>
            _advanced.Values.Should().OnlyContain(advanced => advanced.Kind == DateTimeKind.Utc);
    }
}
