// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_MssqlRelationalWriteExceptionClassifier
{
    private MssqlRelationalWriteExceptionClassifier _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new MssqlRelationalWriteExceptionClassifier();
    }

    [Test]
    public void It_classifies_unique_constraint_violations_using_the_final_constraint_name()
    {
        var exception = CreateSqlException(
            2627,
            "Violation of UNIQUE KEY constraint 'UX_Student_SchoolId_StudentUniqueId_1a2b3c4d5e'. "
                + "Cannot insert duplicate key in object 'edfi.Student'."
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification.Should().BeOfType<RelationalWriteExceptionClassification.UniqueConstraintViolation>();
        classification
            .As<RelationalWriteExceptionClassification.UniqueConstraintViolation>()
            .ConstraintName.Should()
            .Be("UX_Student_SchoolId_StudentUniqueId_1a2b3c4d5e");
    }

    [Test]
    public void It_classifies_unique_index_violations_using_the_final_index_name()
    {
        var exception = CreateSqlException(
            2601,
            "Cannot insert duplicate key row in object 'dbo.Student' with unique index "
                + "'UX_Student_SchoolId_StudentUniqueId_1a2b3c4d5e'."
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification.Should().BeOfType<RelationalWriteExceptionClassification.UniqueConstraintViolation>();
        classification
            .As<RelationalWriteExceptionClassification.UniqueConstraintViolation>()
            .ConstraintName.Should()
            .Be("UX_Student_SchoolId_StudentUniqueId_1a2b3c4d5e");
    }

    [Test]
    public void It_classifies_foreign_key_violations_using_the_final_constraint_name()
    {
        var exception = CreateSqlException(
            547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint "
                + "\"FK_Student_SchoolReference_DocumentId_SchoolId_a1b2c3d4e5\"."
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeOfType<RelationalWriteExceptionClassification.ForeignKeyConstraintViolation>();
        classification
            .As<RelationalWriteExceptionClassification.ForeignKeyConstraintViolation>()
            .ConstraintName.Should()
            .Be("FK_Student_SchoolReference_DocumentId_SchoolId_a1b2c3d4e5");
    }

    [Test]
    public void It_classifies_reference_constraint_violations_from_delete_statements()
    {
        var exception = CreateSqlException(
            547,
            "The DELETE statement conflicted with the REFERENCE constraint "
                + "\"FK_StudentSchoolAssociation_Student_DocumentId_StudentDocumentId_a1b2c3d4e5\"."
                + " The conflict occurred in database \"EdFi_Datastore\", table \"dms.StudentSchoolAssociation\"."
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeOfType<RelationalWriteExceptionClassification.ForeignKeyConstraintViolation>();
        classification
            .As<RelationalWriteExceptionClassification.ForeignKeyConstraintViolation>()
            .ConstraintName.Should()
            .Be("FK_StudentSchoolAssociation_Student_DocumentId_StudentDocumentId_a1b2c3d4e5");
    }

    [Test]
    public void It_falls_back_when_constraint_name_parsing_fails()
    {
        var exception = CreateSqlException(
            547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint."
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeSameAs(RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance);
    }

    [Test]
    public void It_falls_back_for_non_foreign_key_constraint_number_547_messages()
    {
        var exception = CreateSqlException(
            547,
            "The UPDATE statement conflicted with the CHECK constraint \"CK_Student_SchoolId\"."
        );

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeSameAs(RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance);
    }

    [Test]
    public void It_returns_deterministic_fallback_for_unmapped_sql_server_write_errors()
    {
        var exception = CreateSqlException(8152, "String or binary data would be truncated.");

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeTrue();
        classification
            .Should()
            .BeSameAs(RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance);
    }

    [TestCase(1205)]
    [TestCase(1222)]
    public void It_does_not_classify_retryable_deadlock_or_contention_exceptions(int errorNumber)
    {
        var exception = CreateSqlException(errorNumber, "Transaction retry should stay owned by DMS-996.");

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeFalse();
        classification.Should().BeNull();
    }

    [Test]
    public void It_ignores_non_sql_server_db_exceptions()
    {
        var exception = new FakeNonSqlDbException("not sql server");

        var classified = _sut.TryClassify(exception, out var classification);

        classified.Should().BeFalse();
        classification.Should().BeNull();
    }

    [TestCase(1205)]
    [TestCase(1222)]
    public void It_reports_transient_failure_for_deadlock_or_lock_timeout_errors(int errorNumber)
    {
        var exception = CreateSqlException(errorNumber, "Transient SQL Server condition.");

        _sut.IsTransientFailure(exception).Should().BeTrue();
    }

    [TestCase(547)]
    [TestCase(2627)]
    [TestCase(2601)]
    [TestCase(8152)]
    public void It_does_not_report_transient_failure_for_non_transient_error_numbers(int errorNumber)
    {
        var exception = CreateSqlException(errorNumber, "Non-transient SQL Server condition.");

        _sut.IsTransientFailure(exception).Should().BeFalse();
    }

    [Test]
    public void It_does_not_report_transient_failure_for_non_sql_server_db_exceptions()
    {
        _sut.IsTransientFailure(new FakeNonSqlDbException("not sql server")).Should().BeFalse();
    }

    /// <summary>
    /// Creates a real <see cref="SqlException"/> for testing.
    /// <see cref="SqlException"/> has no public constructor; instances are built via
    /// <see cref="RuntimeHelpers.GetUninitializedObject"/> + field reflection so that tests
    /// remain unit-testable without a live SQL Server. Reflection is acceptable here in test
    /// code only — production code uses a direct <c>is SqlException</c> cast.
    /// </summary>
    private static SqlException CreateSqlException(int number, string message)
    {
        var sqlError = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlError, message);

        var errorList = new List<object> { sqlError };
        var errorCollection = (SqlErrorCollection)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errorCollection, errorList);

        var sqlException = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sqlException, errorCollection);

        return sqlException;
    }

    private sealed class FakeNonSqlDbException(string message) : DbException(message);
}
