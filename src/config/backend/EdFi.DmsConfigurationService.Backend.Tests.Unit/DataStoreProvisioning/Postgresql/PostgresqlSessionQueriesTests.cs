// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The termination request's guards (D-22, Step 3.4) without a server: the server-side wait is whole
/// milliseconds rounded up, and a request the engine cannot serve is refused before any connection is
/// opened, which an unreachable endpoint proves by the exception type.
/// </summary>
public class PostgresqlSessionQueriesTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Username=dms;Password=unused;Timeout=1";

    private static readonly DmsServerSessionId _session = new(4242, DateTimeOffset.UnixEpoch);

    private static PostgresqlDmsTargetEngine UnreachableEngine() =>
        new(
            new PostgresqlAdminConnection(UnreachableConnectionString, TimeSpan.FromSeconds(30)),
            new PostgresqlDmsEngineSettings(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                Path.Combine(AppContext.BaseDirectory, "psql-not-available")
            ),
            TimeProvider.System,
            NullLogger.Instance
        );

    [TestFixture]
    public class Given_deadlines_converted_to_a_server_wait
    {
        private long _oneTick;
        private long _fractionalMilliseconds;
        private long _wholeSeconds;

        [SetUp]
        public void Setup()
        {
            _oneTick = PostgresqlSessionQueries.TimeoutMillisecondsOf(TimeSpan.FromTicks(1));
            _fractionalMilliseconds = PostgresqlSessionQueries.TimeoutMillisecondsOf(
                TimeSpan.FromMilliseconds(1.5)
            );
            _wholeSeconds = PostgresqlSessionQueries.TimeoutMillisecondsOf(TimeSpan.FromSeconds(30));
        }

        [Test]
        public void It_never_waits_less_than_one_millisecond() => _oneTick.Should().Be(1);

        [Test]
        public void It_rounds_up_to_whole_milliseconds() => _fractionalMilliseconds.Should().Be(2);

        [Test]
        public void It_keeps_whole_seconds_exact() => _wholeSeconds.Should().Be(30_000);
    }

    [TestFixture]
    public class Given_termination_requests_the_engine_refuses_before_connecting
    {
        private Exception? _zeroDeadline;
        private Exception? _negativeDeadline;
        private Exception? _overlongDeadline;
        private Exception? _zeroId;
        private Exception? _negativeId;
        private Exception? _idWiderThanAPid;
        private Exception? _servableRequest;

        [OneTimeSetUp]
        public async Task Setup()
        {
            PostgresqlDmsTargetEngine engine = UnreachableEngine();
            _zeroDeadline = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(_session, TimeSpan.Zero, default)
            );
            _negativeDeadline = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(_session, TimeSpan.FromSeconds(-1), default)
            );
            _overlongDeadline = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(
                    _session,
                    PostgresqlSessionQueries.MaximumDeadline + TimeSpan.FromTicks(1),
                    default
                )
            );
            _zeroId = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(_session with { Id = 0 }, TimeSpan.FromSeconds(30), default)
            );
            _negativeId = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(_session with { Id = -1 }, TimeSpan.FromSeconds(30), default)
            );
            _idWiderThanAPid = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(
                    _session with
                    {
                        Id = (long)int.MaxValue + 1,
                    },
                    TimeSpan.FromSeconds(30),
                    default
                )
            );
            _servableRequest = await Record.ExceptionAsync(() =>
                engine.TerminateSessionAsync(_session, TimeSpan.FromSeconds(30), default)
            );
        }

        [Test]
        public void It_refuses_a_zero_deadline() =>
            _zeroDeadline.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_a_negative_deadline() =>
            _negativeDeadline.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_a_deadline_beyond_the_maximum() =>
            _overlongDeadline.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_a_zero_session_id() =>
            _zeroId.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_a_negative_session_id() =>
            _negativeId.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_refuses_an_id_no_backend_pid_can_hold() =>
            _idWiderThanAPid.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_reaches_the_connection_for_a_servable_request() =>
            _servableRequest.Should().BeAssignableTo<NpgsqlException>();
    }

    [TestFixture]
    public class Given_a_session_lookup_without_an_application_name
    {
        private Exception? _blank;

        [OneTimeSetUp]
        public async Task Setup()
        {
            DmsDatabaseName database = DmsDatabaseName.TryCreate("postgres", out DmsDatabaseName name)
                ? name
                : throw new InvalidOperationException("The name does not satisfy the grammar.");
            _blank = await Record.ExceptionAsync(() =>
                UnreachableEngine().FindSessionsAsync(database, " ", default)
            );
        }

        [Test]
        public void It_refuses_before_connecting() => _blank.Should().BeOfType<ArgumentException>();
    }
}
