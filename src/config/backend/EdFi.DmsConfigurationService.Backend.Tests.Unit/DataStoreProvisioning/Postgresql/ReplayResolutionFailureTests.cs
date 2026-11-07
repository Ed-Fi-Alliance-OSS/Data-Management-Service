// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The engine's replay resolution when it cannot finish (D-7b, Step 3.5 review round 2): a session lookup
/// that fails, here against a server that cannot be reached, proves nothing, so the outcome stays unknown,
/// names no session, is not quiescent, and is logged with only its type chain; it never escapes as an
/// exception the orchestrator would classify while a replay session might still live.
/// </summary>
public class ReplayResolutionFailureTests
{
    [TestFixture]
    public class Given_a_resolution_whose_session_lookup_fails
    {
        private readonly CapturingLogger<PostgresqlDmsTargetEngine> _logger = new();
        private DmsCommandOutcome<DmsInstanceIdentity> _outcome = null!;
        private DmsOperationId _operation;

        [OneTimeSetUp]
        public async Task Setup()
        {
            _operation = DmsOperationId.New();
            PostgresqlDmsTargetEngine engine = new(
                new PostgresqlAdminConnection(
                    "Host=127.0.0.1;Port=1;Username=dms;Password=SENTINEL-PASSWORD-91c2;Timeout=1",
                    TimeSpan.FromSeconds(30)
                ),
                new PostgresqlDmsEngineSettings(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromMinutes(5),
                    Path.Combine(AppContext.BaseDirectory, "psql-not-available")
                ),
                TimeProvider.System,
                _logger
            );
            _outcome = await engine.ResolveReplayAsync(
                _operation.StagingDatabaseName,
                _operation,
                new PsqlRunResult.Killed(4321, PsqlKillReason.Cancelled)
            );
        }

        [Test]
        public void It_leaves_the_outcome_unknown_and_unresolvable() =>
            _outcome
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>()
                .Which.Should()
                .BeEquivalentTo(new { Session = (DmsServerSessionId?)null, Quiescent = false });

        [Test]
        public void It_logs_the_failure_with_only_the_operation_and_type_chain()
        {
            LogEntry entry = _logger
                .Entries.Should()
                .ContainSingle(entry => entry.EventId == PostgresqlDmsTargetEngine.ReplayResolutionFailed)
                .Subject;
            entry.Field("Operation").Should().Be(_operation.Value);
            _logger
                .Entries.SelectMany(logged =>
                    logged
                        .Fields.Select(field => field.Value?.ToString() ?? string.Empty)
                        .Prepend(logged.Message)
                )
                .Should()
                .NotContain(text => text.Contains("SENTINEL-PASSWORD-91c2") || text.Contains("127.0.0.1"));
        }
    }
}
