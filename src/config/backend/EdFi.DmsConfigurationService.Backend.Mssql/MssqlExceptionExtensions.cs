// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// Maps SqlException errors to the constraint-violation categories the repositories translate
/// into result types. Error 2627 is a unique/primary-key constraint violation, 2601 a
/// unique-index violation, 547 a foreign-key violation. Constraint, index, and foreign-key names
/// follow the standardized PascalCase conventions (PK_/UX_/FK_/IX_) so the name literals here
/// match the ones the PostgreSQL repositories use.
/// </summary>
/// <remarks>
/// The violated name is read from the position SQL Server quotes it at, never by searching the
/// whole message: a unique-violation message ends with the duplicate value itself ("The duplicate
/// key value is (...)"), and a client-supplied value can spell another constraint's name (DMS-1529).
/// Each <see cref="SqlError"/> is examined on its own, so a batch that reports several errors is
/// classified by any of them rather than by the first one only.
/// </remarks>
internal static partial class MssqlExceptionExtensions
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const int ForeignKeyViolation = 547;

    // 2627: Violation of UNIQUE KEY constraint 'UX_Name'. Cannot insert duplicate key in object
    //       'dmscs.Table'. The duplicate key value is (value).
    //       (also "Violation of PRIMARY KEY constraint 'PK_Name'. ...")
    // The constraint name is the first single-quoted token.
    [GeneratedRegex("^[^']*'(?<name>[^']+)'")]
    private static partial Regex FirstSingleQuotedToken();

    // 2601: Cannot insert duplicate key row in object 'dmscs.Table' with unique index 'UX_Name'.
    //       The duplicate key value is (value).
    // The index name is the second single-quoted token; the object name is the first.
    [GeneratedRegex("^[^']*'[^']*'[^']*'(?<name>[^']+)'")]
    private static partial Regex SecondSingleQuotedToken();

    // 547: The INSERT statement conflicted with the FOREIGN KEY constraint "FK_Name". The conflict
    //      occurred in database "db", table "dmscs.Table", column 'Id'.
    //      (also "The DELETE statement conflicted with the REFERENCE constraint "FK_Name". ...")
    // The constraint name is the first double-quoted token.
    [GeneratedRegex("^[^\"]*\"(?<name>[^\"]+)\"")]
    private static partial Regex FirstDoubleQuotedToken();

    public static bool IsUniqueViolation(this SqlException ex, string constraintOrIndexName) =>
        ex
            .Errors.Cast<SqlError>()
            .Any(error =>
                NamesViolatedConstraint(
                    UniqueViolationConstraintName(error.Number, error.Message),
                    constraintOrIndexName
                )
            );

    public static bool IsForeignKeyViolation(this SqlException ex, string foreignKeyName) =>
        ex
            .Errors.Cast<SqlError>()
            .Any(error =>
                NamesViolatedConstraint(
                    ForeignKeyViolationConstraintName(error.Number, error.Message),
                    foreignKeyName
                )
            );

    /// <summary>
    /// The unique constraint (2627) or unique index (2601) a SQL Server error names, or null when the
    /// error is neither or its message does not carry the name where SQL Server places it.
    /// </summary>
    public static string? UniqueViolationConstraintName(int errorNumber, string message) =>
        errorNumber switch
        {
            UniqueConstraintViolation => CapturedName(FirstSingleQuotedToken(), message),
            UniqueIndexViolation => CapturedName(SecondSingleQuotedToken(), message),
            _ => null,
        };

    /// <summary>
    /// The foreign key a SQL Server error 547 names, or null when the error is not 547 or its
    /// message does not carry the name where SQL Server places it.
    /// </summary>
    public static string? ForeignKeyViolationConstraintName(int errorNumber, string message) =>
        errorNumber == ForeignKeyViolation ? CapturedName(FirstDoubleQuotedToken(), message) : null;

    private static string? CapturedName(Regex pattern, string message)
    {
        Match match = pattern.Match(message);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static bool NamesViolatedConstraint(string? violatedName, string expectedName) =>
        violatedName is not null
        && string.Equals(violatedName, expectedName, StringComparison.OrdinalIgnoreCase);
}
