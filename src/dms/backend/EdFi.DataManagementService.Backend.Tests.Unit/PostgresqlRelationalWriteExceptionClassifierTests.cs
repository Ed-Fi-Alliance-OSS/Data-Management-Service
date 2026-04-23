// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_PostgresqlRelationalWriteExceptionClassifier
{
    private PostgresqlRelationalWriteExceptionClassifier _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new PostgresqlRelationalWriteExceptionClassifier();
    }

    [Test]
    public void It_classifies_unique_constraint_violations_using_the_final_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.UniqueViolation,
            constraintName: "uq_edfi_student_root_identity"
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification.Should().BeOfType<RelationalWriteExceptionClassification.UniqueConstraintViolation>();
        classification
            .As<RelationalWriteExceptionClassification.UniqueConstraintViolation>()
            .ConstraintName.Should()
            .Be("uq_edfi_student_root_identity");
    }

    [Test]
    public void It_classifies_foreign_key_violations_using_the_final_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.ForeignKeyViolation,
            constraintName: "fk_edfi_student_schoolreference_documentid_schoolid"
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeOfType<RelationalWriteExceptionClassification.ForeignKeyConstraintViolation>();
        classification
            .As<RelationalWriteExceptionClassification.ForeignKeyConstraintViolation>()
            .ConstraintName.Should()
            .Be("fk_edfi_student_schoolreference_documentid_schoolid");
    }

    [Test]
    public void It_returns_deterministic_fallback_for_unmapped_postgresql_write_errors()
    {
        var exception = CreatePostgresException(PostgresErrorCodes.CheckViolation);

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeSameAs(RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance);
    }

    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    public void It_does_not_classify_retryable_transaction_exceptions(string sqlState)
    {
        var exception = CreatePostgresException(sqlState);

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeFalse();
        classification.Should().BeNull();
    }

    [Test]
    public void It_falls_back_when_constraint_violation_metadata_does_not_include_a_constraint_name()
    {
        var exception = CreatePostgresException(PostgresErrorCodes.UniqueViolation, constraintName: "");

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeSameAs(RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance);
    }

    [Test]
    public void It_ignores_non_postgresql_db_exceptions()
    {
        var exception = new FakeDbException();

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeFalse();
        classification.Should().BeNull();
    }

    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    public void It_reports_transient_failure_for_deadlock_or_serialization_errors(string sqlState)
    {
        var exception = CreatePostgresException(sqlState);

        _sut.IsTransientFailure(exception).Should().BeTrue();
    }

    [TestCase(PostgresErrorCodes.UniqueViolation)]
    [TestCase(PostgresErrorCodes.ForeignKeyViolation)]
    [TestCase(PostgresErrorCodes.CheckViolation)]
    public void It_does_not_report_transient_failure_for_non_transient_sql_states(string sqlState)
    {
        var exception = CreatePostgresException(sqlState);

        _sut.IsTransientFailure(exception).Should().BeFalse();
    }

    [Test]
    public void It_does_not_report_transient_failure_for_non_postgresql_db_exceptions()
    {
        _sut.IsTransientFailure(new FakeDbException()).Should().BeFalse();
    }

    [Test]
    public void It_reports_foreign_key_violation_for_sql_state_23503_with_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.ForeignKeyViolation,
            constraintName: "fk_edfi_student_schoolreference_documentid_schoolid"
        );

        _sut.IsForeignKeyViolation(exception).Should().BeTrue();
    }

    [Test]
    public void It_reports_foreign_key_violation_for_sql_state_23503_without_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.ForeignKeyViolation,
            constraintName: string.Empty
        );

        _sut.IsForeignKeyViolation(exception).Should().BeTrue();
    }

    [TestCase(PostgresErrorCodes.UniqueViolation)]
    [TestCase(PostgresErrorCodes.CheckViolation)]
    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    public void It_does_not_report_foreign_key_violation_for_other_sql_states(string sqlState)
    {
        var exception = CreatePostgresException(sqlState);

        _sut.IsForeignKeyViolation(exception).Should().BeFalse();
    }

    [Test]
    public void It_does_not_report_foreign_key_violation_for_non_postgresql_db_exceptions()
    {
        _sut.IsForeignKeyViolation(new FakeDbException()).Should().BeFalse();
    }

    [Test]
    public void It_reports_unique_constraint_violation_for_sql_state_23505_with_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.UniqueViolation,
            constraintName: "uq_edfi_student_root_identity"
        );

        _sut.IsUniqueConstraintViolation(exception).Should().BeTrue();
    }

    [Test]
    public void It_reports_unique_constraint_violation_for_sql_state_23505_without_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.UniqueViolation,
            constraintName: string.Empty
        );

        _sut.IsUniqueConstraintViolation(exception).Should().BeTrue();
    }

    [TestCase(PostgresErrorCodes.ForeignKeyViolation)]
    [TestCase(PostgresErrorCodes.CheckViolation)]
    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    public void It_does_not_report_unique_constraint_violation_for_other_sql_states(string sqlState)
    {
        var exception = CreatePostgresException(sqlState);

        _sut.IsUniqueConstraintViolation(exception).Should().BeFalse();
    }

    [Test]
    public void It_does_not_report_unique_constraint_violation_for_non_postgresql_db_exceptions()
    {
        _sut.IsUniqueConstraintViolation(new FakeDbException()).Should().BeFalse();
    }

    private static PostgresException CreatePostgresException(string sqlState, string? constraintName = null)
    {
        return new PostgresException(
            messageText: "simulated write failure",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            detail: string.Empty,
            hint: string.Empty,
            position: 0,
            internalPosition: 0,
            internalQuery: string.Empty,
            where: string.Empty,
            schemaName: "edfi",
            tableName: "Student",
            columnName: string.Empty,
            dataTypeName: string.Empty,
            constraintName: constraintName ?? string.Empty,
            file: "test.sql",
            line: "1",
            routine: "Execute"
        );
    }

    private sealed class FakeDbException : DbException { }
}
