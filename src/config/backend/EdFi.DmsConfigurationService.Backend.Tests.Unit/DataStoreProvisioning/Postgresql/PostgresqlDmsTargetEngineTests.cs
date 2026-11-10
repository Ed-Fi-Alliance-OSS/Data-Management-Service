// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

public class PostgresqlDmsTargetEngineTests
{
    /// <summary>
    /// The fence's bound on the holder session: the instance check and the <c>ALTER DATABASE</c> each run for up to
    /// one command allowance before the termination loop starts, and the loop then has the whole termination bound.
    /// The bound covers all three, and is the orchestrator's allowance for the fence, never more.
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
                    - setting.Command
                    - setting.Command
                )
                .Should()
                .Equal(_settings.Select(setting => setting.Termination));
    }
}
