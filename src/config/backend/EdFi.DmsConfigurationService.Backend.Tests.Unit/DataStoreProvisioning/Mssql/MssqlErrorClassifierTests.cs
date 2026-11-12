// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Sockets;
using System.Reflection;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// SQL Server error-number classification (6.9a): numbers that prove no commit are definite failures of the
/// right class; a client timeout, a cancellation after send, an aborted batch, a fatal severity, and a
/// transport failure leave the outcome in doubt; 4060 is not an access refusal because it also reports a
/// missing database. The <see cref="Microsoft.Data.SqlClient.SqlException"/> paths are exercised against a
/// live server by the integration lane, since the driver offers no way to construct one.
/// </summary>
public class MssqlErrorClassifierTests
{
    private const byte UserSeverity = 16;

    [TestFixture]
    public class Given_numbers_that_prove_the_effect_did_not_commit
    {
        private Dictionary<int, DmsCommandFailureClass?> _classes = [];

        [SetUp]
        public void Setup()
        {
            int[] numbers =
            [
                1801,
                5061,
                5030,
                3702,
                3101,
                5064,
                924,
                3142,
                3159,
                3201,
                3241,
                3242,
                3013,
                4060,
                229,
                262,
                6102,
                911,
                208,
                6106,
                102,
                2627,
                18456,
                18470,
            ];
            _classes = numbers.ToDictionary(
                number => number,
                number => MssqlErrorClassifier.FailureClassOf(number, UserSeverity)
            );
        }

        [Test]
        public void It_classifies_database_already_exists_as_duplicate_name() =>
            _classes[1801].Should().Be(DmsCommandFailureClass.DuplicateName);

        [Test]
        public void It_classifies_an_alter_database_lock_not_placed_as_object_in_use() =>
            _classes[5061].Should().Be(DmsCommandFailureClass.ObjectInUse);

        [Test]
        public void It_classifies_the_other_in_use_errors_as_object_in_use() =>
            new[] { _classes[5030], _classes[3702], _classes[3101] }
                .Should()
                .AllBeEquivalentTo(DmsCommandFailureClass.ObjectInUse);

        [Test]
        public void It_classifies_the_single_user_slot_held_by_another_session_as_object_in_use() =>
            (_classes[5064], _classes[924])
                .Should()
                .Be((DmsCommandFailureClass.ObjectInUse, DmsCommandFailureClass.ObjectInUse));

        [Test]
        public void It_classifies_a_restore_over_an_existing_file_as_rejected() =>
            _classes[3142].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_restore_needing_the_tail_of_the_log_as_rejected() =>
            _classes[3159].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_backup_device_that_cannot_open_as_rejected() =>
            _classes[3201].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_malformed_media_family_as_rejected() =>
            _classes[3241].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_file_that_is_not_a_backup_set_as_rejected() =>
            _classes[3242].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_an_abnormally_terminated_restore_as_rejected() =>
            _classes[3013].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_a_database_the_login_cannot_open_as_rejected_not_privilege() =>
            _classes[4060].Should().Be(DmsCommandFailureClass.Rejected);

        [Test]
        public void It_classifies_permission_denials_as_privilege_missing() =>
            new[] { _classes[229], _classes[262], _classes[6102] }
                .Should()
                .AllBeEquivalentTo(DmsCommandFailureClass.PrivilegeMissing);

        [Test]
        public void It_classifies_a_failed_or_disabled_login_as_privilege_missing() =>
            new[] { _classes[18456], _classes[18470] }
                .Should()
                .AllBeEquivalentTo(DmsCommandFailureClass.PrivilegeMissing);

        [Test]
        public void It_classifies_a_missing_database_object_or_session_as_does_not_exist() =>
            new[] { _classes[911], _classes[208], _classes[6106] }
                .Should()
                .AllBeEquivalentTo(DmsCommandFailureClass.DoesNotExist);

        [Test]
        public void It_classifies_syntax_and_constraint_errors_as_rejected() =>
            new[] { _classes[102], _classes[2627] }
                .Should()
                .AllBeEquivalentTo(DmsCommandFailureClass.Rejected);
    }

    [TestFixture]
    public class Given_errors_that_leave_the_outcome_in_doubt
    {
        private DmsCommandFailureClass? _clientTimeout;
        private DmsCommandFailureClass? _clientCancelled;
        private DmsCommandFailureClass? _batchAborted;
        private DmsCommandFailureClass? _fatalDuplicate;
        private Dictionary<int, DmsCommandFailureClass?> _transport = [];

        [SetUp]
        public void Setup()
        {
            _clientTimeout = MssqlErrorClassifier.FailureClassOf(-2, 11);
            _clientCancelled = MssqlErrorClassifier.FailureClassOf(0, 11);
            _batchAborted = MssqlErrorClassifier.FailureClassOf(3980, UserSeverity);
            _fatalDuplicate = MssqlErrorClassifier.FailureClassOf(1801, 20);
            int[] transport = [-1, 2, 40, 53, 64, 121, 233, 10053, 10054, 10060, 11001];
            _transport = transport.ToDictionary(
                number => number,
                number => MssqlErrorClassifier.FailureClassOf(number, UserSeverity)
            );
        }

        [Test]
        public void It_leaves_a_client_timeout_in_doubt() => _clientTimeout.Should().BeNull();

        [Test]
        public void It_leaves_a_cancellation_after_send_in_doubt() => _clientCancelled.Should().BeNull();

        [Test]
        public void It_leaves_an_aborted_batch_in_doubt() => _batchAborted.Should().BeNull();

        [Test]
        public void It_leaves_any_number_at_fatal_severity_in_doubt() => _fatalDuplicate.Should().BeNull();

        [Test]
        public void It_leaves_every_transport_failure_in_doubt() =>
            _transport.Values.Should().AllSatisfy(value => value.Should().BeNull());
    }

    [TestFixture]
    public class Given_the_connection_and_access_predicates
    {
        [Test]
        public void It_treats_severity_20_and_above_as_connection_class() =>
            (
                MssqlErrorClassifier.IsConnectionClass(4060, 19),
                MssqlErrorClassifier.IsConnectionClass(4060, 20),
                MssqlErrorClassifier.IsConnectionClass(596, 21)
            )
                .Should()
                .Be((false, true, true));

        [Test]
        public void It_treats_transport_numbers_as_connection_class_at_any_severity() =>
            (
                MssqlErrorClassifier.IsConnectionClass(10054, 0),
                MssqlErrorClassifier.IsConnectionClass(-2, 0),
                MssqlErrorClassifier.IsConnectionClass(0, 0)
            )
                .Should()
                .Be((true, false, false));

        [Test]
        public void It_treats_a_transport_exception_without_a_server_error_as_connection_failure() =>
            (
                MssqlErrorClassifier.IsConnectionFailure(
                    new InvalidOperationException("outer", new IOException("reset"))
                ),
                MssqlErrorClassifier.IsConnectionFailure(new SocketException()),
                MssqlErrorClassifier.IsConnectionFailure(new TimeoutException())
            )
                .Should()
                .Be((true, true, false));

        [Test]
        public void It_maps_only_an_invalid_object_name_to_a_missing_relation() =>
            (MssqlErrorClassifier.IsMissingRelation(208), MssqlErrorClassifier.IsMissingRelation(911))
                .Should()
                .Be((true, false));

        [Test]
        public void It_maps_only_an_invalid_column_name_to_a_missing_column() =>
            (
                MssqlErrorClassifier.IsMissingColumn(207),
                Array.Exists([208, 529, 229, 230, 245, 0, -2, 10054], MssqlErrorClassifier.IsMissingColumn),
                MssqlErrorClassifier.IsMissingRelation(207)
            )
                .Should()
                .Be((true, false, false));

        [Test]
        public void It_maps_only_a_refused_explicit_conversion_to_a_refused_conversion() =>
            (
                MssqlErrorClassifier.IsConversionRefused(529),
                Array.Exists([207, 208, 245, 8114, 0, -2, 10054], MssqlErrorClassifier.IsConversionRefused)
            )
                .Should()
                .Be((true, false));

        [Test]
        public void It_maps_only_compile_time_shape_errors_to_a_refused_populated_predicate() =>
            (
                Array.TrueForAll([207, 206, 8116], MssqlErrorClassifier.IsPredicateShapeRefusal),
                Array.Exists(
                    [208, 245, 529, 229, 230, 916, 0, -2, 10054, 8114],
                    MssqlErrorClassifier.IsPredicateShapeRefusal
                )
            )
                .Should()
                .Be((true, false));

        [Test]
        public void It_treats_denials_and_failed_logins_as_access_refusals() =>
            new[] { 229, 230, 262, 297, 300, 916, 3110, 5011, 6102, 15247, 18456, 18470 }
                .Should()
                .OnlyContain(number => MssqlErrorClassifier.IsAccessRefusal(number));

        [Test]
        public void It_does_not_treat_4060_as_an_access_refusal() =>
            MssqlErrorClassifier.IsAccessRefusal(4060).Should().BeFalse();

        [Test]
        public void It_reports_no_error_number_without_a_server_error() =>
            MssqlErrorClassifier.ErrorNumberOf(new InvalidOperationException()).Should().BeNull();
    }

    /// <summary>
    /// AC 11: the error numbers of a <c>RESTORE</c> rejected in its own response (the specific error, then
    /// <c>3013</c>). A deadlock victim or a temporary memory shortage (<c>701</c>) can succeed on retry; every other
    /// rejection, out-of-space ones (<c>1105</c>, <c>3257</c>) included, stays permanent.
    /// </summary>
    [TestFixture]
    public class Given_the_error_numbers_of_a_rejected_restore
    {
        [Test]
        public void It_treats_a_deadlock_victim_as_retryable() =>
            MssqlErrorClassifier.IsRetryableRejection([1205, 3013]).Should().BeTrue();

        [Test]
        public void It_treats_a_memory_shortage_as_retryable() =>
            MssqlErrorClassifier.IsRetryableRejection([701, 3013]).Should().BeTrue();

        [Test]
        public void It_keeps_every_other_rejection_permanent() =>
            new int[][] { [3234, 3013], [3110, 3013], [3013], [1801], [1105, 3013], [3257, 3013] }
                .Select(numbers => MssqlErrorClassifier.IsRetryableRejection(numbers))
                .Should()
                .AllBeEquivalentTo(false);

        [Test]
        public void It_keeps_a_memory_shortage_definite_only_below_the_fatal_severity() =>
            (MssqlErrorClassifier.FailureClassOf(701, 17), MssqlErrorClassifier.FailureClassOf(701, 20))
                .Should()
                .Be((DmsCommandFailureClass.Rejected, (DmsCommandFailureClass?)null));
    }

    /// <summary>
    /// H-02: the rename's and the fence's classification of a <see cref="SqlException"/> keeps whether the command took
    /// effect apart from whether a retry can succeed. A deadlock victim (<c>1205</c>) and a temporary memory shortage
    /// (<c>701</c>) are definite and retryable; a refused login or permission stays definite and not retryable. The
    /// driver offers no constructor, so the exceptions are built through its internal factory; a driver change that
    /// removes it fails this fixture loudly rather than passing it.
    /// </summary>
    [TestFixture]
    public class Given_a_server_error_from_a_rename_or_a_fence
    {
        private static readonly DmsServerSessionId _session = new(51, DateTimeOffset.UnixEpoch);

        internal static SqlException Rejected(int number)
        {
            ConstructorInfo errorConstructor = typeof(SqlError)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .OrderByDescending(constructor => constructor.GetParameters().Length)
                .First();
            object?[] arguments =
            [
                .. errorConstructor
                    .GetParameters()
                    .Select(parameter =>
                        parameter.Name switch
                        {
                            "infoNumber" => number,
                            "errorClass" => UserSeverity,
                            _ when parameter.ParameterType == typeof(string) => string.Empty,
                            _ when parameter.ParameterType.IsValueType => Activator.CreateInstance(
                                parameter.ParameterType
                            ),
                            _ => null,
                        }
                    ),
            ];
            SqlError error = (SqlError)errorConstructor.Invoke(arguments);
            SqlErrorCollection errors = (SqlErrorCollection)
                Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
            typeof(SqlErrorCollection)
                .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(errors, [error]);
            MethodInfo create = typeof(SqlException)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method =>
                    method.Name == "CreateException"
                    && method
                        .GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .SequenceEqual([typeof(SqlErrorCollection), typeof(string)])
                );
            SqlException exception = (SqlException)create.Invoke(null, [errors, "17.0.0"])!;
            (exception.Number, exception.Class).Should().Be((number, UserSeverity));
            return exception;
        }

        private static (DmsCommandFailureClass?, bool?, string?) Fence(int number) =>
            MssqlDeletionQueries.Classify(Rejected(number), _session, MssqlDeletionQueries.FenceStep) switch
            {
                DmsCommandOutcome.Failed failed => (
                    failed.Failure.Class,
                    failed.Failure.Retryable,
                    failed.Failure.Diagnostic.ProviderErrorCode
                ),
                _ => (null, null, null),
            };

        private static (DmsCommandFailureClass?, bool?, string?) Rename(int number) =>
            MssqlPublicationQueries.Classify<DmsInstanceIdentity>(
                Rejected(number),
                _session,
                MssqlPublicationQueries.RenameStep
            ) switch
            {
                DmsCommandOutcome<DmsInstanceIdentity>.Failed failed => (
                    failed.Failure.Class,
                    failed.Failure.Retryable,
                    failed.Failure.Diagnostic.ProviderErrorCode
                ),
                _ => (null, null, null),
            };

        [Test]
        public void It_marks_a_deadlock_victim_and_a_memory_shortage_retryable() =>
            new[] { Rename(1205), Rename(701), Fence(1205), Fence(701) }
                .Should()
                .Equal(
                    (DmsCommandFailureClass.Rejected, true, "1205"),
                    (DmsCommandFailureClass.Rejected, true, "701"),
                    (DmsCommandFailureClass.Rejected, true, "1205"),
                    (DmsCommandFailureClass.Rejected, true, "701")
                );

        [Test]
        public void It_keeps_refusals_and_other_rejections_not_retryable() =>
            new[] { Rename(5011), Fence(5011), Rename(3701) }
                .Should()
                .Equal(
                    (DmsCommandFailureClass.PrivilegeMissing, false, "5011"),
                    (DmsCommandFailureClass.PrivilegeMissing, false, "5011"),
                    (DmsCommandFailureClass.Rejected, false, "3701")
                );
    }
}
