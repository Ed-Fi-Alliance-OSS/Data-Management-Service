// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
    public void It_classifies_postgresql_18_restrict_violations_as_foreign_key_violations()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.RestrictViolation,
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
    public void It_maps_a_postgresql_18_restrict_violation_on_delete_to_a_reference_conflict()
    {
        const string ConstraintName = "fk_edfi_student_schoolreference_documentid_schoolid";
        var referencingResource = new QualifiedResourceName("Ed-Fi", "Student");
        var resolver = A.Fake<IRelationalDeleteConstraintResolver>();
        A.CallTo(() => resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, ConstraintName))
            .Returns(referencingResource);

        var result = RelationalDeleteExecution.MapFailure(
            CreatePostgresException(PostgresErrorCodes.RestrictViolation, ConstraintName),
            _sut,
            resolver,
            CreateModelSet(),
            NullLogger.Instance,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("restrict-violation-delete"),
            DeleteTargetKind.Document
        );

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([referencingResource.ResourceName]));
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
    [TestCase(PostgresErrorCodes.LockNotAvailable)]
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
    [TestCase(PostgresErrorCodes.LockNotAvailable)]
    public void It_reports_transient_failure_for_deadlock_serialization_or_lock_timeout_errors(
        string sqlState
    )
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

    [Test]
    public void It_reports_foreign_key_violation_for_sql_state_23001_without_constraint_name()
    {
        var exception = CreatePostgresException(
            PostgresErrorCodes.RestrictViolation,
            constraintName: string.Empty
        );

        _sut.IsForeignKeyViolation(exception).Should().BeTrue();
    }

    [TestCase(PostgresErrorCodes.UniqueViolation)]
    [TestCase(PostgresErrorCodes.CheckViolation)]
    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    [TestCase(PostgresErrorCodes.LockNotAvailable)]
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

    private static DerivedRelationalModelSet CreateModelSet()
    {
        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "School"),
            "1.0.0",
            false
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0",
                "v1",
                "schema-hash",
                1,
                [1, 2, 3],
                [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                [resourceKey]
            ),
            SqlDialect.Pgsql,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
            [],
            [],
            [],
            [],
            []
        );
    }

    private sealed class FakeDbException : DbException { }
}
