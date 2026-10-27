// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.TimingEvidence;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The recorder's bookkeeping, without timing assertions: a run either records one sample per arm
/// per round, in a warm-up file and a measured file, with one timestamp pair per round and a header
/// naming both files and the collector's build, or, when any response in either phase is not the one
/// its arm stands for, aborts and writes nothing.
/// </summary>
public class TimingEvidenceRecorderTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"timing-recorder-{Guid.NewGuid():N}");

    private static void Remove(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestFixture]
    public class Given_a_timing_run_whose_arm_receives_a_response_it_does_not_stand_for_in_the_warm_up
    {
        private string _directory = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Arrange()
        {
            _directory = NewDirectory();
            TimingRunSettings settings = new(_directory, "smoke", 1, 3, 1, "collector-build");
            TimingArm<int>[] arms =
            [
                new("expected", () => Task.FromResult(1), response => response == 1),
                new("unexpected", () => Task.FromResult(2), response => response == 1),
            ];
            _act = () =>
                TimingEvidenceRecorder.RunAsync(settings, "test", new Dictionary<string, string>(), arms);
        }

        [TearDown]
        public void TearDown() => Remove(_directory);

        [Test]
        public async Task It_aborts_the_run_naming_the_arm() =>
            await _act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'unexpected'*");

        [Test]
        public async Task It_writes_no_files()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>();
            Directory.Exists(_directory).Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_a_timing_run_whose_arm_receives_a_response_it_does_not_stand_for_after_the_warm_up
    {
        private const int WarmupRounds = 2;
        private string _directory = null!;
        private Func<Task> _act = null!;

        [SetUp]
        public void Arrange()
        {
            _directory = NewDirectory();
            TimingRunSettings settings = new(_directory, "smoke", WarmupRounds, 3, 1, "collector-build");
            int calls = 0;
            TimingArm<int>[] arms =
            [
                new("steady", () => Task.FromResult(1), response => response == 1),
                // Right through the warm-up, then wrong on its first measured request.
                new(
                    "drifting",
                    () => Task.FromResult(++calls > WarmupRounds ? 2 : 1),
                    response => response == 1
                ),
            ];
            _act = () =>
                TimingEvidenceRecorder.RunAsync(settings, "test", new Dictionary<string, string>(), arms);
        }

        [TearDown]
        public void TearDown() => Remove(_directory);

        [Test]
        public async Task It_aborts_the_run_naming_the_arm() =>
            await _act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'drifting'*");

        [Test]
        public async Task It_writes_no_files_not_even_the_finished_warm_up()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>();
            Directory.Exists(_directory).Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_a_timing_run_on_a_recording_clock
    {
        private const int WarmupRounds = 2;
        private const int Rounds = 3;
        private const int Seed = 11;

        /// <summary>
        /// A clock that records every read: <c>T</c> for the timestamp, <c>U</c> for the wall clock.
        /// Each timestamp read advances 100 ticks at a frequency of one million, so every timed span
        /// (one read before the request, one after) is exactly 100 us.
        /// </summary>
        private sealed class RecordingClock(List<string> events)
        {
            public const long Frequency = 1_000_000;
            public static readonly DateTime Start = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

            private long _ticks = 5_000_000;
            private DateTime _utc = Start;

            public TimingClock Clock => new(Timestamp, Frequency, UtcNow);

            private long Timestamp()
            {
                events.Add("T");
                _ticks += 100;
                return _ticks;
            }

            private DateTime UtcNow()
            {
                events.Add("U");
                DateTime now = _utc;
                _utc = _utc.AddMilliseconds(1);
                return now;
            }
        }

        // An arm that records its request (S) and the check of its response (E).
        private static TimingArm<string> RecordedArm(string name, List<string> events) =>
            new(
                name,
                () =>
                {
                    events.Add($"S:{name}");
                    return Task.FromResult(name);
                },
                response =>
                {
                    events.Add($"E:{name}");
                    return response == name;
                }
            );

        // The order the current implementation draws: one System.Random(seed).Shuffle per round,
        // over the warm-up rounds and then the measured rounds, and no other draw.
        private static List<string> DrawnOrder(int seed, string[] names, int rounds)
        {
            Random order = new(seed);
            int[] positions = [.. Enumerable.Range(0, names.Length)];
            List<string> sequence = [];
            for (int round = 0; round < rounds; round++)
            {
                order.Shuffle(positions);
                sequence.AddRange(positions.Select(position => names[position]));
            }
            return sequence;
        }

        private static string[][] DataRows(string path) =>
            [.. File.ReadAllLines(path).Skip(1).Select(line => line.Split(','))];

        private static readonly string[] _names = ["first", "second", "third"];

        private string _directory = null!;
        private List<string> _events = null!;
        private TimingRunFiles _files = null!;

        [SetUp]
        public async Task Act()
        {
            _directory = NewDirectory();
            _events = [];
            TimingRunSettings settings = new(
                _directory,
                "pilot",
                WarmupRounds,
                Rounds,
                Seed,
                "collector-build"
            );
            TimingArm<string>[] arms = [.. _names.Select(name => RecordedArm(name, _events))];
            _files = await TimingEvidenceRecorder.RunAsync(
                settings,
                "test",
                new Dictionary<string, string>(),
                arms,
                new RecordingClock(_events).Clock
            );
        }

        [TearDown]
        public void TearDown() => Remove(_directory);

        private JsonNode Header() => JsonNode.Parse(File.ReadAllText(_files.HeaderPath))!;

        [Test]
        public void It_names_the_measured_and_warm_up_files_apart() =>
            new[] { Path.GetFileName(_files.SamplesPath), Path.GetFileName(_files.WarmupPath) }
                .Should()
                .Equal("pilot-test-20261024T120000Z.csv", "pilot-warmup-test-20261024T120000Z.csv");

        [Test]
        public void It_writes_both_files_with_the_same_columns() =>
            new[] { File.ReadLines(_files.SamplesPath).First(), File.ReadLines(_files.WarmupPath).First() }
                .Should()
                .AllBe("round,arm,position,elapsed_us,round_started_qpc,round_started_utc");

        [Test]
        public void It_records_one_measured_sample_per_arm_per_round() =>
            DataRows(_files.SamplesPath).Should().HaveCount(Rounds * _names.Length);

        [Test]
        public void It_records_one_warm_up_sample_per_arm_per_warm_up_round() =>
            DataRows(_files.WarmupPath).Should().HaveCount(WarmupRounds * _names.Length);

        [Test]
        public void It_numbers_each_phases_rounds_from_zero() =>
            new[]
            {
                DataRows(_files.WarmupPath).Select(fields => fields[0]).Distinct(),
                DataRows(_files.SamplesPath).Select(fields => fields[0]).Distinct(),
            }
                .Should()
                .BeEquivalentTo(
                    new[] { new[] { "0", "1" }, new[] { "0", "1", "2" } },
                    options => options.WithStrictOrdering()
                );

        [Test]
        public void It_keeps_the_order_the_seed_draws_across_warm_up_and_measured_rounds() =>
            _events
                .Where(entry => entry.StartsWith("S:", StringComparison.Ordinal))
                .Select(entry => entry[2..])
                .Should()
                .Equal(DrawnOrder(Seed, _names, WarmupRounds + Rounds));

        [Test]
        public void It_writes_the_drawn_order_into_the_arm_and_position_columns() =>
            DataRows(_files.WarmupPath)
                .Concat(DataRows(_files.SamplesPath))
                .Select(fields => $"{fields[1]}@{fields[2]}")
                .Should()
                .Equal(
                    DrawnOrder(Seed, _names, WarmupRounds + Rounds)
                        .Select((name, index) => $"{name}@{index % _names.Length}")
                );

        [Test]
        public void It_stamps_each_round_once_after_its_shuffle_and_outside_every_timed_span()
        {
            // The run reads the wall clock once at its start and once at its end. Each round reads
            // the timestamp and the wall clock once, then for each arm: the timestamp, the request,
            // the timestamp, and only then the check of the response.
            List<string> drawn = DrawnOrder(Seed, _names, WarmupRounds + Rounds);
            List<string> expected = ["U"];
            for (int round = 0; round < WarmupRounds + Rounds; round++)
            {
                expected.AddRange(["T", "U"]);
                foreach (string name in drawn.Skip(round * _names.Length).Take(_names.Length))
                {
                    expected.AddRange(["T", $"S:{name}", "T", $"E:{name}"]);
                }
            }
            expected.Add("U");
            _events.Should().Equal(expected);
        }

        [Test]
        public void It_repeats_one_timestamp_pair_on_every_row_of_a_round_and_a_new_pair_in_each_round()
        {
            foreach (string path in new[] { _files.WarmupPath, _files.SamplesPath })
            {
                string[][] rows = DataRows(path);
                rows.GroupBy(fields => fields[0])
                    .Should()
                    .OnlyContain(round =>
                        round.Select(fields => $"{fields[4]}|{fields[5]}").Distinct().Count() == 1
                    );
                rows.Select(fields => fields[4]).Distinct().Should().HaveCount(rows.Length / _names.Length);
            }
        }

        [Test]
        public void It_writes_the_round_utc_in_the_round_trip_form()
        {
            string utc = DataRows(_files.SamplesPath)[0][5];
            DateTime
                .ParseExact(utc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .Kind.Should()
                .Be(DateTimeKind.Utc);
        }

        [Test]
        public void It_times_each_request_on_the_injected_clock() =>
            DataRows(_files.SamplesPath).Select(fields => fields[3]).Should().AllBe("100.000");

        [Test]
        public void It_pins_the_schema_both_files_and_both_row_counts_in_the_header()
        {
            JsonNode header = Header();
            new object[]
            {
                header["schema"]!.GetValue<string>(),
                header["samples_file"]!.GetValue<string>(),
                header["warmup_file"]!.GetValue<string>(),
                header["measured_rows"]!.GetValue<int>(),
                header["warmup_rows"]!.GetValue<int>(),
                header["stopwatch_frequency"]!.GetValue<long>(),
            }
                .Should()
                .Equal(
                    "timing-evidence/2",
                    Path.GetFileName(_files.SamplesPath),
                    Path.GetFileName(_files.WarmupPath),
                    Rounds * _names.Length,
                    WarmupRounds * _names.Length,
                    RecordingClock.Frequency
                );
        }

        [Test]
        public void It_reports_both_row_counts() =>
            new[] { _files.Samples, _files.WarmupSamples }
                .Should()
                .Equal(Rounds * _names.Length, WarmupRounds * _names.Length);
    }

    [TestFixture]
    public class Given_a_timing_run_whose_arms_receive_their_expected_responses
    {
        private const int Rounds = 3;
        private string _directory = null!;
        private TimingRunFiles _files = null!;

        [SetUp]
        public async Task Act()
        {
            _directory = NewDirectory();
            TimingRunSettings settings = new(_directory, "smoke", 2, Rounds, 7, "collector-build");
            TimingArm<int>[] arms =
            [
                new("first", () => Task.FromResult(1), response => response == 1),
                new("second", () => Task.FromResult(2), response => response == 2),
            ];
            _files = await TimingEvidenceRecorder.RunAsync(
                settings,
                "test",
                new Dictionary<string, string> { ["target_build"] = "target-build" },
                arms
            );
        }

        [TearDown]
        public void TearDown() => Remove(_directory);

        [Test]
        public void It_records_one_sample_per_arm_per_round() =>
            File.ReadAllLines(_files.SamplesPath).Should().HaveCount(1 + (Rounds * 2));

        [Test]
        public void It_starts_the_samples_with_the_header_row_and_no_byte_order_mark() =>
            File.ReadAllBytes(_files.SamplesPath).Take(5).Should().Equal("round"u8.ToArray());

        [Test]
        public void It_records_every_arm_once_in_every_round() =>
            File.ReadAllLines(_files.SamplesPath)
                .Skip(1)
                .Select(line => line.Split(','))
                .GroupBy(fields => fields[0])
                .Should()
                .HaveCount(Rounds)
                .And.OnlyContain(round =>
                    round.Select(fields => fields[1]).Order().SequenceEqual(new[] { "first", "second" })
                );

        private JsonNode Header() => JsonNode.Parse(File.ReadAllText(_files.HeaderPath))!;

        [Test]
        public void It_names_the_collectors_build() =>
            Header()["collector_commit"]!.GetValue<string>().Should().Be("collector-build");

        [Test]
        public void It_keeps_the_target_build_apart_from_the_collectors_build() =>
            Header()["context"]!["target_build"]!.GetValue<string>().Should().Be("target-build");
    }
}
