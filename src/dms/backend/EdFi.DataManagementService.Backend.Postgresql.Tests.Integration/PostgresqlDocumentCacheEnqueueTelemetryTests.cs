// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Parallelizable]
[Category("PostgresqlIntegration")]
[Category("DocumentCacheEnqueueTelemetry")]
public class Given_Postgresql_DocumentCacheEnqueueTelemetry
{
    private readonly PostgresqlRelationalWriteExceptionClassifier _writeExceptionClassifier = new();

    [Test]
    public void It_classifies_projection_work_foreign_key_failures_as_work_persistence_failures()
    {
        var exception = CreateException(
            PostgresErrorCodes.ForeignKeyViolation,
            "insert or update on table \"DocumentProjectionWork\" violates foreign key constraint \"FK_DocumentProjectionWork_Document\"",
            tableName: "DocumentProjectionWork",
            constraintName: "FK_DocumentProjectionWork_Document"
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
        var exception = CreateException(
            PostgresErrorCodes.InsufficientPrivilege,
            "permission denied for function dms.\"TF_Document_EnqueueProjection\""
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

    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    [TestCase(PostgresErrorCodes.LockNotAvailable)]
    public void It_does_not_classify_transient_provider_failures_without_enqueue_artifacts(string sqlState)
    {
        var exception = CreateException(sqlState, "canonical resource write transient failure.");

        bool classified = DocumentCacheEnqueueFailureClassifier.TryClassify(
            exception,
            _writeExceptionClassifier,
            out DocumentCacheEnqueueFailureCategory category,
            out _
        );

        classified.Should().BeFalse();
        category.Should().Be(default(DocumentCacheEnqueueFailureCategory));
    }

    [TestCase(PostgresErrorCodes.DeadlockDetected)]
    [TestCase(PostgresErrorCodes.SerializationFailure)]
    [TestCase(PostgresErrorCodes.LockNotAvailable)]
    public void It_classifies_transient_enqueue_artifact_failures_as_provider_timeouts(string sqlState)
    {
        var exception = CreateException(
            sqlState,
            "transient provider failure while inserting into dms.DocumentProjectionWork"
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

    private static PostgresException CreateException(
        string sqlState,
        string message,
        string tableName = "Document",
        string constraintName = ""
    ) =>
        new(
            messageText: message,
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            detail: string.Empty,
            hint: string.Empty,
            position: 0,
            internalPosition: 0,
            internalQuery: string.Empty,
            where: string.Empty,
            schemaName: "dms",
            tableName: tableName,
            columnName: string.Empty,
            dataTypeName: string.Empty,
            constraintName: constraintName,
            file: "test.sql",
            line: "1",
            routine: "Execute"
        );
}
