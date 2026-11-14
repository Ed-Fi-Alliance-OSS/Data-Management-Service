// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.ProvisioningTestValues;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

public class PostgresqlDmsTargetEngineTests
{
    /// <summary>
    /// The fence's bound on the holder session: the instance check and the <c>ALTER DATABASE</c> each run for up to
    /// one command allowance before the termination loop starts, the loop then has the whole termination bound, and
    /// after it a termination's overrun, its verification read and the deciding read have one allowance each. The
    /// live outcome is pinned by <c>PostgresqlDeleteTests</c>; the orchestrator's allowance adds the holder gate
    /// wait on top of it.
    /// </summary>
    [TestFixture]
    public class Given_the_fence_statements_use_their_whole_command_allowance
    {
        private static readonly (TimeSpan Termination, TimeSpan Command)[] _settings =
        [
            (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)),
            (TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(120)),
            (TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(1)),
        ];

        [Test]
        public void It_leaves_the_whole_termination_bound_to_the_loop() =>
            _settings
                .Select(setting =>
                    PostgresqlDmsTargetEngine.FenceDeadline(setting.Termination, setting.Command)
                    - (setting.Command * 5)
                )
                .Should()
                .Equal(_settings.Select(setting => setting.Termination));
    }

    /// <summary>
    /// The marker's dispatch boundary without a server: a cancellation found before the marker is handed to the
    /// driver is reported as not dispatched, as on SQL Server, so the orchestrator keeps the target renamed and
    /// unmarked instead of a marker in doubt. The connection is never opened, so nothing could have been sent.
    /// </summary>
    [TestFixture]
    public class Given_the_token_is_cancelled_before_the_marker_is_sent
    {
        private Exception? _outcome;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();
            await using NpgsqlConnection connection = new();
            _outcome = await Record.ExceptionAsync(() =>
                PostgresqlPublicationQueries.AssignMarkerAsync(
                    connection,
                    16385,
                    Guid.NewGuid(),
                    Session,
                    cancelled.Token
                )
            );
        }

        [Test]
        public void It_reports_the_marker_as_not_dispatched() =>
            _outcome.Should().BeOfType<DmsCommandNotDispatchedException>();
    }
}
