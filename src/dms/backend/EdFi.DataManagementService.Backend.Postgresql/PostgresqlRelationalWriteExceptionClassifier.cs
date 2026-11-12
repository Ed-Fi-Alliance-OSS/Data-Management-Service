// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
{
    public bool TryClassify(
        DbException exception,
        [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
    )
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not PostgresException postgresException)
        {
            classification = null;
            return false;
        }

        classification = postgresException.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => BuildConstraintClassification(
                postgresException.ConstraintName,
                static constraintName => new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                    constraintName
                )
            ),
            // ON DELETE RESTRICT: PostgreSQL 18 reports restrict_violation (23001), earlier versions 23503.
            PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation =>
                BuildConstraintClassification(
                    postgresException.ConstraintName,
                    static constraintName => new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                        constraintName
                    )
                ),
            PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.LockNotAvailable => null,
            _ => RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance,
        };

        return classification is not null;
    }

    public bool IsForeignKeyViolation(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Rely on SqlState directly so FK violations still map to a 409 DeleteFailureReference
        // even if the constraint name is absent. DMS generates ON DELETE RESTRICT foreign keys,
        // which PostgreSQL 18 reports as restrict_violation (23001) rather than 23503.
        return exception is PostgresException { SqlState: var sqlState }
            && sqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.RestrictViolation;
    }

    public bool IsUniqueConstraintViolation(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Rely on SqlState 23505 directly so unique-violation failures still map to a 409
        // write-conflict even if the constraint name is absent.
        return exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    public bool IsTransientFailure(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is PostgresException { SqlState: var sqlState }
            && sqlState
                is PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.LockNotAvailable;
    }

    private static RelationalWriteExceptionClassification BuildConstraintClassification(
        string? constraintName,
        Func<string, RelationalWriteExceptionClassification.ConstraintViolation> createClassification
    )
    {
        return string.IsNullOrWhiteSpace(constraintName)
            ? RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance
            : createClassification(constraintName);
    }
}
