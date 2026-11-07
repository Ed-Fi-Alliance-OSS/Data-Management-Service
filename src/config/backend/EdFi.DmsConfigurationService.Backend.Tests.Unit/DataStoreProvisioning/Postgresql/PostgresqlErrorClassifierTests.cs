// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// SQLSTATE classification (6.9a): states that prove no commit are definite failures of the right class;
/// connection, shutdown, and cancellation states leave the outcome in doubt; only the SQLSTATE is ever read.
/// </summary>
public class PostgresqlErrorClassifierTests
{
    [TestFixture]
    public class Given_sqlstates_that_prove_the_effect_did_not_commit
    {
        private Dictionary<string, DmsCommandFailureClass?> _classes = [];

        [SetUp]
        public void Setup()
        {
            string[] states =
            [
                "42P04",
                "55006",
                "42501",
                "3D000",
                "42P01",
                "42601",
                "23505",
                "40001",
                "25006",
            ];
            _classes = states.ToDictionary(state => state, PostgresqlErrorClassifier.FailureClassOf);
        }

        [Test]
        public void It_classifies_duplicate_database_as_duplicate_name() =>
            _classes["42P04"].Should().Be(DmsCommandFailureClass.DuplicateName);

        [Test]
        public void It_classifies_object_in_use() =>
            _classes["55006"].Should().Be(DmsCommandFailureClass.ObjectInUse);

        [Test]
        public void It_classifies_insufficient_privilege() =>
            _classes["42501"].Should().Be(DmsCommandFailureClass.PrivilegeMissing);

        [Test]
        public void It_classifies_a_missing_database_as_does_not_exist() =>
            _classes["3D000"].Should().Be(DmsCommandFailureClass.DoesNotExist);

        [Test]
        public void It_classifies_a_missing_table_as_does_not_exist() =>
            _classes["42P01"].Should().Be(DmsCommandFailureClass.DoesNotExist);

        [Test]
        public void It_classifies_a_syntax_error_as_rejected() =>
            _classes["42601"].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_constraint_violation_as_rejected() =>
            _classes["23505"].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_serialization_failure_as_rejected() =>
            _classes["40001"].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_read_only_transaction_error_as_rejected() =>
            _classes["25006"].Should().Be(DmsCommandFailureClass.Rejected);
    }

    [TestFixture]
    public class Given_sqlstates_that_leave_the_outcome_in_doubt
    {
        private Dictionary<string, DmsCommandFailureClass?> _classes = [];
        private Dictionary<string, bool> _connectionClass = [];

        [SetUp]
        public void Setup()
        {
            string[] states =
            [
                "08006",
                "08003",
                "08001",
                "57P01",
                "57P02",
                "57P03",
                "57014",
                "57P04",
                "40003",
            ];
            _classes = states.ToDictionary(state => state, PostgresqlErrorClassifier.FailureClassOf);
            _connectionClass = states.ToDictionary(
                state => state,
                PostgresqlErrorClassifier.IsConnectionClass
            );
        }

        [Test]
        public void It_leaves_every_connection_failure_unclassified() =>
            _classes.Values.Should().AllSatisfy(failureClass => failureClass.Should().BeNull());

        [Test]
        public void It_recognizes_class_08_as_connection_class() =>
            new[] { _connectionClass["08006"], _connectionClass["08003"], _connectionClass["08001"] }
                .Should()
                .AllBeEquivalentTo(true);

        [Test]
        public void It_recognizes_operator_shutdowns_as_connection_class() =>
            new[] { _connectionClass["57P01"], _connectionClass["57P02"], _connectionClass["57P03"] }
                .Should()
                .AllBeEquivalentTo(true);

        [Test]
        public void It_does_not_call_a_cancelled_query_connection_class() =>
            _connectionClass["57014"].Should().BeFalse();

        [Test]
        public void It_does_not_call_a_dropped_database_connection_class() =>
            _connectionClass["57P04"].Should().BeFalse();

        [Test]
        public void It_leaves_statement_completion_unknown_in_doubt() => _classes["40003"].Should().BeNull();

        [Test]
        public void It_does_not_call_statement_completion_unknown_connection_class() =>
            _connectionClass["40003"].Should().BeFalse();
    }

    [TestFixture]
    public class Given_exceptions_to_read_a_sqlstate_from
    {
        private const string Sentinel = "password=hunter2;host=db.internal";

        private string? _direct;
        private string? _nested;
        private string? _malformed;
        private string? _notPostgres;
        private DmsProvisioningDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup()
        {
            _direct = PostgresqlErrorClassifier.SqlStateOf(Error("42P04"));
            _nested = PostgresqlErrorClassifier.SqlStateOf(
                new InvalidOperationException("outer", new NpgsqlException("middle", Error("55006")))
            );
            _malformed = PostgresqlErrorClassifier.SqlStateOf(Error("bad state"));
            _notPostgres = PostgresqlErrorClassifier.SqlStateOf(new TimeoutException(Sentinel));
            _diagnostic = PostgresqlErrorClassifier.Diagnostic(
                new NpgsqlException("outer", Error("42501")),
                "ReadTargetFacts"
            );
        }

        [Test]
        public void It_reads_the_direct_sqlstate() => _direct.Should().Be("42P04");

        [Test]
        public void It_reads_the_first_postgres_error_in_the_chain() => _nested.Should().Be("55006");

        [Test]
        public void It_drops_a_malformed_sqlstate() => _malformed.Should().BeNull();

        [Test]
        public void It_reads_nothing_from_a_non_postgres_exception() => _notPostgres.Should().BeNull();

        [Test]
        public void It_builds_a_diagnostic_from_the_type_chain_and_sqlstate() =>
            (_diagnostic.ExceptionTypeChain, _diagnostic.ProviderErrorCode, _diagnostic.Operation)
                .Should()
                .Be(("Npgsql.NpgsqlException/Npgsql.PostgresException", "42501", "ReadTargetFacts"));

        [Test]
        public void It_never_carries_the_message() =>
            _diagnostic.ToString().Should().NotContain(Sentinel).And.NotContain("hunter2");

        private static PostgresException Error(string sqlState) =>
            new($"ERROR with {Sentinel}", "ERROR", "ERROR", sqlState);
    }

    [TestFixture]
    public class Given_missing_relation_states
    {
        private bool _undefinedTable;
        private bool _invalidSchema;
        private bool _invalidCatalog;

        [SetUp]
        public void Setup()
        {
            _undefinedTable = PostgresqlErrorClassifier.IsMissingRelation("42P01");
            _invalidSchema = PostgresqlErrorClassifier.IsMissingRelation("3F000");
            _invalidCatalog = PostgresqlErrorClassifier.IsMissingRelation("3D000");
        }

        [Test]
        public void It_treats_an_undefined_table_as_missing() => _undefinedTable.Should().BeTrue();

        [Test]
        public void It_treats_an_undefined_schema_as_missing() => _invalidSchema.Should().BeTrue();

        [Test]
        public void It_does_not_treat_a_missing_database_as_a_missing_relation() =>
            _invalidCatalog.Should().BeFalse();
    }

    [TestFixture]
    public class Given_states_that_may_be_a_definite_access_refusal
    {
        private static readonly string[] _refusals = ["42501", "28000", "28P01"];

        // Missing objects, connection loss, cancellation, admin shutdown and a serialization failure are not refusals.
        private static readonly string[] _others =
        [
            "42P01",
            "3F000",
            "3D000",
            "08006",
            "57014",
            "57P01",
            "40001",
        ];

        private bool[] _refusalVerdicts = [];
        private bool[] _otherVerdicts = [];

        [SetUp]
        public void Setup()
        {
            _refusalVerdicts = [.. _refusals.Select(PostgresqlErrorClassifier.IsAccessRefusal)];
            _otherVerdicts = [.. _others.Select(PostgresqlErrorClassifier.IsAccessRefusal)];
        }

        [Test]
        public void It_treats_insufficient_privilege_and_authorization_class_as_refusals() =>
            _refusalVerdicts.Should().AllSatisfy(verdict => verdict.Should().BeTrue());

        [Test]
        public void It_leaves_every_other_state_to_its_own_handling() =>
            _otherVerdicts.Should().AllSatisfy(verdict => verdict.Should().BeFalse());
    }

    [TestFixture]
    public class Given_exceptions_that_may_prove_the_connection_failed
    {
        private bool _connectionClassState;
        private bool _adminShutdownInsideDriverException;
        private bool _transportFailure;
        private bool _socketFailure;
        private bool _cancelledStatement;
        private bool _acknowledgedDriverTimeout;
        private bool _lockNotAvailable;
        private bool _rejectedStatement;
        private bool _unrelatedFailure;

        [SetUp]
        public void Setup()
        {
            _connectionClassState = PostgresqlErrorClassifier.IsConnectionFailure(Error("08006"));
            _adminShutdownInsideDriverException = PostgresqlErrorClassifier.IsConnectionFailure(
                new NpgsqlException("terminating", Error("57P01"))
            );
            _transportFailure = PostgresqlErrorClassifier.IsConnectionFailure(
                new NpgsqlException("Exception while reading from stream", new EndOfStreamException())
            );
            _socketFailure = PostgresqlErrorClassifier.IsConnectionFailure(
                new NpgsqlException("Exception while writing to stream", new SocketException(10054))
            );
            _cancelledStatement = PostgresqlErrorClassifier.IsConnectionFailure(
                new OperationCanceledException("cancelled", Error("57014"))
            );
            _acknowledgedDriverTimeout = PostgresqlErrorClassifier.IsConnectionFailure(
                new NpgsqlException("Exception while reading from stream", new TimeoutException())
            );
            _lockNotAvailable = PostgresqlErrorClassifier.IsConnectionFailure(Error("55P03"));
            _rejectedStatement = PostgresqlErrorClassifier.IsConnectionFailure(Error("42601"));
            _unrelatedFailure = PostgresqlErrorClassifier.IsConnectionFailure(
                new InvalidOperationException("not a connection error")
            );
        }

        [Test]
        public void It_treats_a_connection_class_state_as_a_connection_failure() =>
            _connectionClassState.Should().BeTrue();

        [Test]
        public void It_reads_the_state_through_the_driver_exception() =>
            _adminShutdownInsideDriverException.Should().BeTrue();

        [Test]
        public void It_treats_a_transport_failure_as_a_connection_failure() =>
            (_transportFailure, _socketFailure).Should().Be((true, true));

        [Test]
        public void It_does_not_treat_a_cancelled_statement_as_a_connection_failure() =>
            _cancelledStatement.Should().BeFalse();

        [Test]
        public void It_does_not_treat_an_acknowledged_driver_timeout_as_a_connection_failure() =>
            _acknowledgedDriverTimeout.Should().BeFalse();

        [Test]
        public void It_does_not_treat_a_server_rejection_as_a_connection_failure() =>
            (_lockNotAvailable, _rejectedStatement, _unrelatedFailure).Should().Be((false, false, false));

        private static PostgresException Error(string sqlState) =>
            new("ERROR with password=hunter2", "ERROR", "ERROR", sqlState);
    }
}
