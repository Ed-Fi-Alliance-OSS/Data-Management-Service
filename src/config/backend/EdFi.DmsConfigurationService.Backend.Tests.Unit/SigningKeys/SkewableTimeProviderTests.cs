// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// Pins the test clock R3.1 relies on: a wall-clock step moves neither the monotonic timestamp nor a pending timer, and
/// elapsed time moves both clocks and fires due timers.
/// </summary>
public class SkewableTimeProviderTests
{
    private static readonly DateTimeOffset _start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [TestFixture]
    public class Given_a_wall_clock_step
    {
        private SkewableTimeProvider _time = null!;
        private long _timestampBefore;
        private bool _timerFiredAtTheStep;
        private DateTimeOffset _wallAfterTheStep;
        private long _timestampAfterTheStep;

        [SetUp]
        public void Act()
        {
            _time = new SkewableTimeProvider(new FakeTimeProvider(_start));
            _timestampBefore = _time.GetTimestamp();
            using ITimer timer = _time.CreateTimer(
                _ => _timerFiredAtTheStep = true,
                null,
                TimeSpan.FromSeconds(1),
                Timeout.InfiniteTimeSpan
            );

            _time.StepWallClock(TimeSpan.FromHours(2));
            _wallAfterTheStep = _time.GetUtcNow();
            _timestampAfterTheStep = _time.GetTimestamp();
        }

        [Test]
        public void It_moves_the_wall_clock() => _wallAfterTheStep.Should().Be(_start.AddHours(2));

        [Test]
        public void It_leaves_the_timestamp_unchanged() =>
            _timestampAfterTheStep.Should().Be(_timestampBefore);

        [Test]
        public void It_does_not_fire_a_pending_timer() => _timerFiredAtTheStep.Should().BeFalse();
    }

    [TestFixture]
    public class Given_elapsed_time_after_a_wall_clock_step
    {
        private DateTimeOffset _wall;
        private TimeSpan _elapsed;
        private bool _timerFired;

        [SetUp]
        public void Act()
        {
            var time = new SkewableTimeProvider(new FakeTimeProvider(_start));
            long started = time.GetTimestamp();
            using ITimer timer = time.CreateTimer(
                _ => _timerFired = true,
                null,
                TimeSpan.FromSeconds(1),
                Timeout.InfiniteTimeSpan
            );
            time.StepWallClock(TimeSpan.FromHours(-2));

            time.Advance(TimeSpan.FromSeconds(1));
            _wall = time.GetUtcNow();
            _elapsed = time.GetElapsedTime(started);
        }

        [Test]
        public void It_moves_the_wall_clock_by_the_elapsed_time() =>
            _wall.Should().Be(_start.AddHours(-2).AddSeconds(1));

        [Test]
        public void It_moves_the_timestamp_by_the_elapsed_time() =>
            _elapsed.Should().Be(TimeSpan.FromSeconds(1));

        [Test]
        public void It_fires_the_timer_when_it_is_due() => _timerFired.Should().BeTrue();
    }
}
