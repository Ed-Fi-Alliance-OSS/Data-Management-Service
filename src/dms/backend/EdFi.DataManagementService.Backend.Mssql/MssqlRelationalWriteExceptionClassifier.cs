// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed partial class MssqlRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
{
    private const int UniqueConstraintViolationNumber = 2627;
    private const int UniqueIndexViolationNumber = 2601;
    private const int ForeignKeyConstraintViolationNumber = 547;
    private const int DeadlockVictimNumber = 1205;
    private const int LockRequestTimeoutNumber = 1222;

    public bool TryClassify(
        DbException exception,
        [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
    )
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not SqlException sqlException)
        {
            classification = null;
            return false;
        }

        classification = sqlException.Number switch
        {
            UniqueConstraintViolationNumber => BuildConstraintClassification(
                sqlException.Message,
                UniqueConstraintNameRegex(),
                static constraintName => new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                    constraintName
                )
            ),
            UniqueIndexViolationNumber => BuildConstraintClassification(
                sqlException.Message,
                UniqueIndexNameRegex(),
                static constraintName => new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                    constraintName
                )
            ),
            ForeignKeyConstraintViolationNumber => BuildConstraintClassification(
                sqlException.Message,
                ForeignKeyConstraintNameRegex(),
                static constraintName => new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                    constraintName
                )
            ),
            DeadlockVictimNumber or LockRequestTimeoutNumber => null,
            _ => RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance,
        };

        return classification is not null;
    }

    public bool IsTransientFailure(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is SqlException sqlException
            && sqlException.Number is DeadlockVictimNumber or LockRequestTimeoutNumber;
    }

    private static RelationalWriteExceptionClassification BuildConstraintClassification(
        string message,
        Regex constraintNamePattern,
        Func<string, RelationalWriteExceptionClassification.ConstraintViolation> createClassification
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(constraintNamePattern);
        ArgumentNullException.ThrowIfNull(createClassification);

        Match match = constraintNamePattern.Match(message);
        string? constraintName = match.Success ? match.Groups["constraintName"].Value : null;

        return string.IsNullOrWhiteSpace(constraintName)
            ? RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance
            : createClassification(constraintName);
    }

    [GeneratedRegex(
        """\bconstraint\s+["'](?<constraintName>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UniqueConstraintNameRegex();

    [GeneratedRegex(
        """\bunique\s+index\s+["'](?<constraintName>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UniqueIndexNameRegex();

    [GeneratedRegex(
        """\bforeign\s+key\s+constraint\s+["'](?<constraintName>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ForeignKeyConstraintNameRegex();
}
