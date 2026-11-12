// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// D-22 on SQL Server without a server: request validation, the guarded batch, and the verdict of every row the
/// batch and the later observations can return. A session the admin may not see is never absence, a kill is
/// sent only for a visible session with the expected <c>login_time</c>, and a refusal keeps its number only.
/// </summary>
public class MssqlSessionQueriesTests
{
    private static readonly DateTimeOffset _loginTime = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private static DmsSessionTermination.NotTerminated Hidden() =>
        new(
            DmsSessionTerminationFailure.PrivilegeMissing,
            DmsProvisioningDiagnostic.ForOperation(MssqlSessionQueries.IdentifySessionStep)
        );

    [TestFixture]
    public class Given_termination_requests
    {
        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(32_768L)]
        [TestCase(70_000L)]
        public void It_refuses_an_id_outside_the_smallint_range(long id) =>
            FluentActions
                .Invoking(() =>
                    MssqlSessionQueries.ValidateRequest(
                        new DmsServerSessionId(id, _loginTime),
                        TimeSpan.FromSeconds(1)
                    )
                )
                .Should()
                .Throw<ArgumentOutOfRangeException>();

        [TestCase(1L)]
        [TestCase(57L)]
        [TestCase(32_767L)]
        public void It_accepts_and_returns_a_smallint_id(long id) =>
            MssqlSessionQueries
                .ValidateRequest(new DmsServerSessionId(id, _loginTime), TimeSpan.FromSeconds(1))
                .Should()
                .Be((short)id);

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(3_600_001)]
        public void It_refuses_a_deadline_outside_the_range(int milliseconds) =>
            FluentActions
                .Invoking(() =>
                    MssqlSessionQueries.ValidateRequest(
                        new DmsServerSessionId(57, _loginTime),
                        TimeSpan.FromMilliseconds(milliseconds)
                    )
                )
                .Should()
                .Throw<ArgumentOutOfRangeException>();

        [Test]
        public void It_accepts_the_maximum_deadline() =>
            MssqlSessionQueries
                .ValidateRequest(new DmsServerSessionId(57, _loginTime), MssqlSessionQueries.MaximumDeadline)
                .Should()
                .Be(57);
    }

    [TestFixture]
    public class Given_driver_timeout_vectors
    {
        [TestCase(60_000, 5_000, 5)]
        [TestCase(30_000, 90_000, 30)]
        [TestCase(30_000, 200, 1)]
        [TestCase(30_000, 1_500, 2)]
        [TestCase(30_000, 0, 1)]
        [TestCase(30_000, -1_000, 1)]
        public void It_never_lets_a_request_outlast_the_remaining_budget(
            int commandTimeoutMilliseconds,
            int remainingMilliseconds,
            int expectedSeconds
        ) =>
            MssqlSessionQueries
                .CommandTimeoutWithin(
                    TimeSpan.FromMilliseconds(commandTimeoutMilliseconds),
                    TimeSpan.FromMilliseconds(remainingMilliseconds)
                )
                .Should()
                .Be(expectedSeconds);
    }

    [TestFixture]
    public class Given_a_request_the_engine_must_refuse
    {
        private Exception? _invalidId;
        private Exception? _invalidDeadline;

        [SetUp]
        public async Task Setup()
        {
            // Nothing listens on port 1, so a request that reached the connection would fail with a SqlException.
            MssqlDmsTargetEngine engine = new(
                new MssqlAdminConnection(
                    "Server=127.0.0.1,1;User Id=sa;Password=unused;Connect Timeout=1;TrustServerCertificate=true",
                    TimeSpan.FromSeconds(5)
                ),
                MssqlEngineSettingsSample.With(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                TimeProvider.System,
                NullLogger.Instance
            );
            _invalidId = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(
                    new DmsServerSessionId(0, _loginTime),
                    TimeSpan.FromSeconds(1),
                    default
                )
            );
            _invalidDeadline = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(new DmsServerSessionId(57, _loginTime), TimeSpan.Zero, default)
            );
        }

        [Test]
        public void It_refuses_an_invalid_id_before_connecting() =>
            _invalidId.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_an_invalid_deadline_before_connecting() =>
            _invalidDeadline.Should().BeOfType<ArgumentOutOfRangeException>();
    }

    [TestFixture]
    public class Given_the_guarded_kill_batch
    {
        private string _batch = string.Empty;

        [SetUp]
        public void Setup() => _batch = MssqlSessionQueries.GuardedKill(57);

        [Test]
        public void It_kills_the_id_as_a_number_exactly_once() => _batch.Split("KILL ").Length.Should().Be(2);

        [Test]
        public void It_writes_the_id_as_a_plain_number() => _batch.Should().Contain("KILL 57;");

        [Test]
        public void It_kills_only_under_the_visibility_and_match_guard() =>
            _batch
                .IndexOf("IF @visible = 1 AND @matched = 1", StringComparison.Ordinal)
                .Should()
                .BeInRange(0, _batch.IndexOf("KILL 57;", StringComparison.Ordinal));

        [Test]
        public void It_catches_the_kill_refusal_as_a_number() =>
            _batch.Should().Contain("SET @killError = ERROR_NUMBER();");

        [Test]
        public void It_matches_the_session_by_id_and_login_time() =>
            _batch.Should().Contain("s.login_time = @loginTime").And.Contain("s.session_id = @sessionId");

        [Test]
        public void It_refuses_a_non_positive_id() =>
            FluentActions
                .Invoking(() => MssqlSessionQueries.GuardedKill(0))
                .Should()
                .Throw<ArgumentOutOfRangeException>();
    }

    [TestFixture]
    public class Given_observation_vectors
    {
        // The observation enum is internal, so the expectation is its name.
        [TestCase(null, 1, nameof(MssqlSessionObservation.IdentityHidden))]
        [TestCase(0, 1, nameof(MssqlSessionObservation.IdentityHidden))]
        [TestCase(0, null, nameof(MssqlSessionObservation.IdentityHidden))]
        [TestCase(1, null, nameof(MssqlSessionObservation.Absent))]
        [TestCase(1, 0, nameof(MssqlSessionObservation.SessionIdReused))]
        [TestCase(1, 1, nameof(MssqlSessionObservation.Present))]
        public void It_reads_the_visibility_before_the_match(int? visible, int? matched, string expected) =>
            MssqlSessionQueries.ObservationOf(visible, matched).ToString().Should().Be(expected);
    }

    [TestFixture]
    public class Given_guarded_kill_rows
    {
        private static DmsSessionTermination.NotTerminated Refused(
            DmsSessionTerminationFailure reason,
            string number
        ) =>
            new(
                reason,
                new DmsProvisioningDiagnostic(string.Empty, number, MssqlSessionQueries.TerminateSessionStep)
            );

        private static IEnumerable<TestCaseData> Rows()
        {
            yield return Row("hidden guard", (0, null, null, 0, null), Hidden());
            yield return Row("hidden guard over a listed session", (0, 1, null, 0, 1), Hidden());
            yield return Row(
                "not listed",
                (1, null, null, 1, null),
                new DmsSessionTermination.AlreadyAbsent()
            );
            yield return Row("id reused", (1, 0, null, 1, 0), new DmsSessionTermination.AlreadyAbsent());
            yield return Row(
                "killed and gone",
                (1, 1, null, 1, null),
                new DmsSessionTermination.Terminated()
            );
            yield return Row(
                "killed and the id reused",
                (1, 1, null, 1, 0),
                new DmsSessionTermination.Terminated()
            );
            yield return Row("killed and still listed", (1, 1, null, 1, 1), null);
            yield return Row("killed and visibility lost", (1, 1, null, 0, null), Hidden());
            yield return Row(
                "no ALTER ANY CONNECTION",
                (1, 1, 6102, 1, 1),
                Refused(DmsSessionTerminationFailure.PrivilegeMissing, "6102")
            );
            yield return Row(
                "ended before the kill",
                (1, 1, 6106, 1, null),
                new DmsSessionTermination.AlreadyAbsent()
            );
            yield return Row(
                "ended before the kill and the id reused",
                (1, 1, 6106, 1, 0),
                new DmsSessionTermination.AlreadyAbsent()
            );
            yield return Row("ended before the kill and visibility lost", (1, 1, 6106, 0, null), Hidden());
            yield return Row(
                "no such session yet still listed",
                (1, 1, 6106, 1, 1),
                Refused(DmsSessionTerminationFailure.Refused, "6106")
            );
            yield return Row(
                "own session",
                (1, 1, 6104, 1, 1),
                Refused(DmsSessionTerminationFailure.Refused, "6104")
            );
            yield return Row(
                "system session",
                (1, 1, 6107, 1, 1),
                Refused(DmsSessionTerminationFailure.Refused, "6107")
            );
        }

        // The row type is internal, so each case carries its five columns.
        private static TestCaseData Row(
            string name,
            (int? Visible, int? Matched, int? KillError, int? VisibleAfter, int? MatchedAfter) row,
            DmsSessionTermination? expected
        ) =>
            new TestCaseData(
                row.Visible,
                row.Matched,
                row.KillError,
                row.VisibleAfter,
                row.MatchedAfter,
                expected
            ).SetName($"It_judges_{name.Replace(' ', '_')}");

        [TestCaseSource(nameof(Rows))]
        public void It_judges(
            int? visible,
            int? matched,
            int? killError,
            int? visibleAfter,
            int? matchedAfter,
            DmsSessionTermination? expected
        ) =>
            MssqlSessionQueries
                .VerdictOf(new MssqlGuardedKillRow(visible, matched, killError, visibleAfter, matchedAfter))
                .Should()
                .Be(expected);
    }

    [TestFixture]
    public class Given_observations_after_the_kill
    {
        [Test]
        public void It_proves_absence() =>
            MssqlSessionQueries
                .AfterKill(MssqlSessionObservation.Absent)
                .Should()
                .Be(new DmsSessionTermination.Terminated());

        [Test]
        public void It_proves_absence_when_the_id_was_reused() =>
            MssqlSessionQueries
                .AfterKill(MssqlSessionObservation.SessionIdReused)
                .Should()
                .Be(new DmsSessionTermination.Terminated());

        [Test]
        public void It_keeps_observing_while_the_session_is_listed() =>
            MssqlSessionQueries.AfterKill(MssqlSessionObservation.Present).Should().BeNull();

        [Test]
        public void It_refuses_when_visibility_is_lost() =>
            MssqlSessionQueries.AfterKill(MssqlSessionObservation.IdentityHidden).Should().Be(Hidden());
    }
}
