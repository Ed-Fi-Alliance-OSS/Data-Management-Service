// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// F-05 (D-5): the staging database is created from the pristine <c>template0</c> in <c>UTF8</c>, with no locale
/// clause, so nothing local to <c>template1</c> reaches it and a session on <c>template1</c> cannot refuse it.
/// </summary>
public class PostgresqlStagingQueriesTests
{
    [TestFixture]
    public class Given_a_staging_database_name
    {
        private string _name = string.Empty;
        private string _statement = string.Empty;

        [SetUp]
        public void Setup()
        {
            DmsDatabaseName staging = DmsOperationId.New().StagingDatabaseName;
            _name = staging.Value;
            _statement = PostgresqlStagingQueries.CreateDatabaseStatement(staging);
        }

        [Test]
        public void It_creates_the_staging_database_from_template0_in_utf8() =>
            _statement.Should().Be($"CREATE DATABASE \"{_name}\" TEMPLATE template0 ENCODING 'UTF8';");
    }

    /// <summary>
    /// H-02: the classification every PostgreSQL mutation shares (staging create, rename, fence, drops) keeps two facts
    /// apart. A rejection in the command's own response is definite whatever its SQLSTATE; whether it is retryable comes
    /// from <see cref="PostgresqlErrorClassifier.IsRetryableRejection(string)"/> alone. A refusal for lack of a privilege
    /// stays definite and not retryable, and a cancelled statement stays in doubt.
    /// </summary>
    [TestFixture]
    public class Given_a_server_error_from_an_administrative_command
    {
        private static readonly DmsServerSessionId _session = new(42, DateTimeOffset.UnixEpoch);

        private static (DmsCommandFailureClass?, bool?, string?) Classified(string sqlState) =>
            PostgresqlStagingQueries.Classify(
                new PostgresException("rejected", "ERROR", "ERROR", sqlState),
                _session,
                PostgresqlStagingQueries.CreateStagingStep
            ) switch
            {
                DmsCommandOutcome.Failed failed => (
                    failed.Failure.Class,
                    failed.Failure.Retryable,
                    failed.Failure.Diagnostic.ProviderErrorCode
                ),
                _ => (null, null, null),
            };

        [Test]
        public void It_marks_conditions_that_clear_on_their_own_retryable() =>
            new[] { "40P01", "55P03", "53200", "53300" }
                .Select(Classified)
                .Should()
                .Equal(
                    (DmsCommandFailureClass.Rejected, true, "40P01"),
                    (DmsCommandFailureClass.Rejected, true, "55P03"),
                    (DmsCommandFailureClass.Rejected, true, "53200"),
                    (DmsCommandFailureClass.Rejected, true, "53300")
                );

        [Test]
        public void It_keeps_privilege_and_other_rejections_not_retryable() =>
            new[] { "42501", "42P04", "22023" }
                .Select(Classified)
                .Should()
                .Equal(
                    (DmsCommandFailureClass.PrivilegeMissing, false, "42501"),
                    (DmsCommandFailureClass.DuplicateName, false, "42P04"),
                    (DmsCommandFailureClass.Rejected, false, "22023")
                );

        [Test]
        public void It_leaves_a_cancelled_statement_in_doubt() =>
            PostgresqlStagingQueries
                .Classify(
                    new PostgresException("cancelled", "ERROR", "ERROR", "57014"),
                    _session,
                    PostgresqlStagingQueries.CreateStagingStep
                )
                .Should()
                .Be(new DmsCommandOutcome.Unknown(_session));
    }
}
