// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_MssqlDocumentCacheWriterDeleteRaceClassifier
{
    [TestCase(
        "The INSERT statement conflicted with the FOREIGN KEY constraint "
            + "\"FK_DocumentCache_Document_DocumentId\"."
    )]
    [TestCase(
        "The DELETE statement conflicted with the REFERENCE constraint "
            + "\"FK_DocumentProjectionWork_Document_DocumentId\"."
    )]
    [TestCase("Localized or reworded SQL Server 547 message without constraint-kind phrasing.")]
    public void It_treats_sql_server_fk_or_reference_547_failures_as_retryable_delete_races(string message)
    {
        SqlException exception = CreateSqlException(547, message);

        MssqlDocumentCacheWriterDeleteRaceClassifier.IsRetryableDeleteRace(exception).Should().BeTrue();
    }

    [Test]
    public void It_treats_document_cache_uuid_trigger_failure_as_a_retryable_delete_race()
    {
        SqlException exception = CreateSqlException(
            50000,
            DocumentCacheInventoryDefinition.DocumentCacheTriggers.MssqlValidateDocumentUuidFailureMessage
        );

        MssqlDocumentCacheWriterDeleteRaceClassifier.IsRetryableDeleteRace(exception).Should().BeTrue();
    }

    [Test]
    public void It_does_not_treat_sql_server_check_constraint_547_failures_as_retryable_delete_races()
    {
        SqlException exception = CreateSqlException(
            547,
            "The UPDATE statement conflicted with the CHECK constraint \"CK_DocumentCache_ContentVersion\"."
        );

        MssqlDocumentCacheWriterDeleteRaceClassifier.IsRetryableDeleteRace(exception).Should().BeFalse();
    }

    [Test]
    public void It_does_not_treat_unrelated_throw_50000_failures_as_retryable_delete_races()
    {
        SqlException exception = CreateSqlException(
            50000,
            "dms.DocumentCacheState.ProjectionLifecycleState has unsupported value for projection enqueue."
        );

        MssqlDocumentCacheWriterDeleteRaceClassifier.IsRetryableDeleteRace(exception).Should().BeFalse();
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
