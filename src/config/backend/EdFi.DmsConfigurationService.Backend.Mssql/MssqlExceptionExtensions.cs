// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql;

/// <summary>
/// Maps SqlException error numbers to the constraint-violation categories the
/// repositories translate into result types. Error 2627 is a unique/primary-key
/// constraint violation, 2601 a unique-index violation, 547 a foreign-key violation.
/// Constraint, index, and foreign-key names are created lowercase in the schema so the
/// name literals here match the ones the PostgreSQL repositories use.
/// </summary>
internal static class MssqlExceptionExtensions
{
    public static bool IsUniqueViolation(this SqlException ex, string constraintOrIndexName) =>
        ex.Number is 2627 or 2601
        && ex.Message.Contains(constraintOrIndexName, StringComparison.OrdinalIgnoreCase);

    public static bool IsForeignKeyViolation(this SqlException ex, string foreignKeyName) =>
        ex.Number == 547 && ex.Message.Contains(foreignKeyName, StringComparison.OrdinalIgnoreCase);
}
