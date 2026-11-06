// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The version probe of 7.3 item 5: the <c>--version</c> print parses to a major and minor, a distribution
/// suffix and a prerelease tag are tolerated, anything else is unparsable, and a missing executable, an
/// error exit, the probe's own bound, and the caller's cancellation are each reported as the spec names them.
/// </summary>
public class PsqlVersionProbeTests
{
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    [TestFixture]
    public class Given_version_prints
    {
        private Dictionary<string, (bool Parsed, int Major, int Minor)> _results = null!;

        [SetUp]
        public void Setup()
        {
            string[] prints =
            [
                "psql (PostgreSQL) 18.1",
                "psql (PostgreSQL) 17.6 (Ubuntu 17.6-1.pgdg24.04+1)",
                "psql (PostgreSQL) 18beta1",
                "psql (PostgreSQL) 18.1\nsecond line is ignored",
                "  psql (PostgreSQL) 16.4\r\n",
                "",
                "pg_dump (PostgreSQL) 18.1",
                "psql (PostgreSQL) x.y",
                "PostgreSQL 18.1",
                "psql (PostgreSQL) 99999999999.1",
                "garbage",
            ];
            _results = prints.ToDictionary(
                print => print,
                print =>
                {
                    bool parsed = PsqlVersionProbe.TryParse(print, out int major, out int minor);
                    return (parsed, major, minor);
                }
            );
        }

        [Test]
        public void It_parses_a_release_version() =>
            _results["psql (PostgreSQL) 18.1"].Should().Be((true, 18, 1));

        [Test]
        public void It_ignores_a_distribution_suffix() =>
            _results["psql (PostgreSQL) 17.6 (Ubuntu 17.6-1.pgdg24.04+1)"].Should().Be((true, 17, 6));

        [Test]
        public void It_gives_a_prerelease_tag_minor_zero() =>
            _results["psql (PostgreSQL) 18beta1"].Should().Be((true, 18, 0));

        [Test]
        public void It_reads_only_the_first_line_and_trims_it()
        {
            _results["psql (PostgreSQL) 18.1\nsecond line is ignored"].Should().Be((true, 18, 1));
            _results["  psql (PostgreSQL) 16.4\r\n"].Should().Be((true, 16, 4));
        }

        [Test]
        public void It_rejects_everything_that_is_not_a_psql_announcement()
        {
            _results[""].Should().Be((false, 0, 0));
            _results["pg_dump (PostgreSQL) 18.1"].Should().Be((false, 0, 0));
            _results["psql (PostgreSQL) x.y"].Should().Be((false, 0, 0));
            _results["PostgreSQL 18.1"].Should().Be((false, 0, 0));
            _results["psql (PostgreSQL) 99999999999.1"].Should().Be((false, 0, 0));
            _results["garbage"].Should().Be((false, 0, 0));
        }

        [Test]
        public void It_rejects_null() => PsqlVersionProbe.TryParse(null, out _, out _).Should().BeFalse();
    }

    [TestFixture]
    public class Given_fakes_that_exit_on_their_own
    {
        private FakePsql _announcing = null!;
        private FakePsql _garbage = null!;
        private FakePsql _failing = null!;
        private PsqlVersionProbeResult _announced = null!;
        private PsqlVersionProbeResult _unparsable = null!;
        private PsqlVersionProbeResult _failed = null!;
        private PsqlVersionProbeResult _missing = null!;

        [SetUp]
        public async Task Setup()
        {
            _announcing = FakePsql.WritingStandardOutput(["psql (PostgreSQL) 18.1"], 0);
            _garbage = FakePsql.WritingStandardOutput(["not a psql at all"], 0);
            _failing = FakePsql.WritingStandardOutput(["psql (PostgreSQL) 18.1"], 1);
            string missing = Path.Combine(_announcing.Directory, "no-such-psql");

            _announced = await Probe(_announcing.ExecutablePath);
            _unparsable = await Probe(_garbage.ExecutablePath);
            _failed = await Probe(_failing.ExecutablePath);
            _missing = await Probe(missing);
        }

        [TearDown]
        public void TearDown()
        {
            _announcing.Dispose();
            _garbage.Dispose();
            _failing.Dispose();
        }

        [Test]
        public void It_parses_the_announced_version() =>
            _announced.Should().Be(new PsqlVersionProbeResult.Parsed(18, 1));

        [Test]
        public void It_reports_a_clean_exit_without_an_announcement_as_unparsable() =>
            _unparsable.Should().Be(new PsqlVersionProbeResult.Unparsable());

        [Test]
        public void It_reports_an_error_exit_without_reading_the_output() =>
            _failed
                .Should()
                .Be(new PsqlVersionProbeResult.Failed(PsqlProbeFailure.ExitedWithError, string.Empty));

        [Test]
        public void It_reports_a_missing_executable_with_the_runtime_type_chain()
        {
            PsqlVersionProbeResult.Failed failed = _missing
                .Should()
                .BeOfType<PsqlVersionProbeResult.Failed>()
                .Subject;
            failed.Reason.Should().Be(PsqlProbeFailure.StartFailed);
            failed.ExceptionTypeChain.Should().Contain("Win32Exception");
        }

        private static Task<PsqlVersionProbeResult> Probe(string path) =>
            PsqlVersionProbe
                .ProbeAsync(
                    path,
                    PsqlVersionProbe.DefaultTimeout,
                    TimeProvider.System,
                    CancellationToken.None
                )
                .WaitAsync(_bound);
    }

    [TestFixture]
    public class Given_a_fake_that_never_exits_and_the_probe_bound_elapses
    {
        private FakePsql _fake = null!;
        private PsqlVersionProbeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string signal = Path.Combine(
                Path.GetTempPath(),
                "dms-1438-probe-signal-" + Guid.NewGuid().ToString("N")
            );
            _fake = FakePsql.Blocking(signal);
            FakeTimeProvider time = new();
            Task<PsqlVersionProbeResult> probe = PsqlVersionProbe.ProbeAsync(
                _fake.ExecutablePath,
                PsqlVersionProbe.DefaultTimeout,
                time,
                CancellationToken.None
            );
            await FakePsql.WaitForFileAsync(signal, _bound);
            probe.IsCompleted.Should().BeFalse("the fake blocks until the bound elapses");
            time.Advance(PsqlVersionProbe.DefaultTimeout);
            _result = await probe.WaitAsync(_bound);
            File.Delete(signal);
        }

        [TearDown]
        public void TearDown() => _fake.Dispose();

        [Test]
        public void It_reports_the_bound_as_a_timeout() =>
            _result.Should().Be(new PsqlVersionProbeResult.Failed(PsqlProbeFailure.TimedOut, string.Empty));
    }

    [TestFixture]
    public class Given_a_fake_that_never_exits_and_the_caller_cancels
    {
        private FakePsql _fake = null!;
        private Exception? _exception;

        [SetUp]
        public async Task Setup()
        {
            string signal = Path.Combine(
                Path.GetTempPath(),
                "dms-1438-probe-signal-" + Guid.NewGuid().ToString("N")
            );
            _fake = FakePsql.Blocking(signal);
            using CancellationTokenSource cancellation = new();
            Task<PsqlVersionProbeResult> probe = PsqlVersionProbe.ProbeAsync(
                _fake.ExecutablePath,
                PsqlVersionProbe.DefaultTimeout,
                TimeProvider.System,
                cancellation.Token
            );
            await FakePsql.WaitForFileAsync(signal, _bound);
            await cancellation.CancelAsync();
            _exception = await Record.ExceptionAsync(() => probe.WaitAsync(_bound));
            File.Delete(signal);
        }

        [TearDown]
        public void TearDown() => _fake.Dispose();

        [Test]
        public void It_propagates_the_cancellation() =>
            _exception.Should().BeAssignableTo<OperationCanceledException>();
    }
}
