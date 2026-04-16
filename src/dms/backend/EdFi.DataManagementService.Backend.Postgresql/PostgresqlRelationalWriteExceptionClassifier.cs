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
            PostgresErrorCodes.ForeignKeyViolation => BuildConstraintClassification(
                postgresException.ConstraintName,
                static constraintName => new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                    constraintName
                )
            ),
            PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure => null,
            _ => RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance,
        };

        return classification is not null;
    }

    public bool IsForeignKeyViolation(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // Rely on SqlState 23503 directly so FK violations still map to a 409
        // DeleteFailureReference even if the constraint name is absent.
        return exception is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
    }

    public bool IsTransientFailure(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is PostgresException { SqlState: var sqlState }
            && sqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;
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
