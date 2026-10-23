// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Tests.TimingEvidence;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The recorder's bookkeeping, without timing assertions: a run either records one sample per arm
/// per round and a header naming the collector's build, or, when any response is not the one its
/// arm stands for, aborts and writes nothing.
/// </summary>
public class TimingEvidenceRecorderTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"timing-recorder-{Guid.NewGuid():N}");

    [TestFixture]
    public class Given_a_timing_run_whose_arm_receives_a_response_it_does_not_stand_for
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
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

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
        public void TearDown() => Directory.Delete(_directory, recursive: true);

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
