// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Parallelizable]
[Category("MssqlIntegration")]
[Category("DocumentCacheEnqueueTelemetry")]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_DocumentCacheEnqueueTelemetry
{
    private readonly MssqlRelationalWriteExceptionClassifier _writeExceptionClassifier = new();

    [Test]
    public void It_classifies_projection_work_foreign_key_failures_as_work_persistence_failures()
    {
        SqlException exception = CreateSqlException(
            547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_DocumentProjectionWork_Document\". The conflict occurred in table \"dms.DocumentProjectionWork\"."
        );

        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            exception,
            _writeExceptionClassifier,
            out DocumentCacheEnqueueFailureCategory category,
            out string message
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
        message.Should().Contain("DocumentProjectionWork");
    }

    [Test]
    public void It_classifies_enqueue_trigger_provider_failures_as_enqueue_trigger_unavailable()
    {
        SqlException exception = CreateSqlException(
            229,
            "The EXECUTE permission was denied on the object 'TF_Document_EnqueueProjection', database 'edfi', schema 'dms'."
        );

        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            exception,
            _writeExceptionClassifier,
            out DocumentCacheEnqueueFailureCategory category,
            out string message
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.EnqueueTriggerUnavailable);
        message.Should().Contain("TF_Document_EnqueueProjection");
    }

    [TestCase(1205)]
    [TestCase(1222)]
    [TestCase(3960)]
    public void It_does_not_classify_transient_provider_failures_without_enqueue_artifacts(int errorNumber)
    {
        SqlException exception = CreateSqlException(
            errorNumber,
            "canonical resource write transient failure."
        );

        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            exception,
            _writeExceptionClassifier,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeFalse();
        category.Should().Be(default(DocumentCacheEnqueueFailureCategory));
    }

    [TestCase(1205)]
    [TestCase(1222)]
    [TestCase(3960)]
    public void It_classifies_transient_enqueue_artifact_failures_as_provider_timeouts(int errorNumber)
    {
        SqlException exception = CreateSqlException(
            errorNumber,
            "Transient provider failure while inserting into dms.DocumentProjectionWork."
        );

        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            exception,
            _writeExceptionClassifier,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeTrue();
        category.Should().Be(DocumentCacheEnqueueFailureCategory.ProviderTimeout);
    }

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
}
