// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Narrows an already-typed natural-key probe value to the CLR type its provider parameter needs.
/// </summary>
/// <remarks>
/// The caller converts identity strings once through <c>RelationalScalarLiteralParser</c> — the same
/// converter the write flattener uses for stored column values — so these overloads normally receive
/// exactly the CLR type the parser produced. They accept the safe widenings (<c>int</c> where a
/// <c>bigint</c> column is expected, for instance) and otherwise throw, because a probe value of the
/// wrong CLR type would not fail: it would silently miss and surface as "reference not found".
/// </remarks>
internal static class RelationalProbeValue
{
    public static string ToStringValue(object value, int columnIndex) =>
        value as string ?? throw Mismatch(value, columnIndex, "string");

    public static int ToInt32(object value, int columnIndex) =>
        value switch
        {
            int intValue => intValue,
            short shortValue => shortValue,
            _ => throw Mismatch(value, columnIndex, "int"),
        };

    public static long ToInt64(object value, int columnIndex) =>
        value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            _ => throw Mismatch(value, columnIndex, "long"),
        };

    public static decimal ToDecimal(object value, int columnIndex) =>
        value switch
        {
            decimal decimalValue => decimalValue,
            long longValue => longValue,
            int intValue => intValue,
            _ => throw Mismatch(value, columnIndex, "decimal"),
        };

    public static bool ToBoolean(object value, int columnIndex) =>
        value is bool booleanValue ? booleanValue : throw Mismatch(value, columnIndex, "bool");

    public static DateOnly ToDate(object value, int columnIndex) =>
        value switch
        {
            DateOnly dateOnlyValue => dateOnlyValue,
            DateTime dateTimeValue => DateOnly.FromDateTime(dateTimeValue),
            _ => throw Mismatch(value, columnIndex, "DateOnly"),
        };

    /// <summary>
    /// Normalizes to a UTC <see cref="DateTime"/>. PostgreSQL's <c>timestamptz</c> binding rejects any
    /// other kind, and <c>RelationalScalarLiteralParser</c> already produces UTC for both the trailing-Z
    /// and the explicit-offset forms; an <see cref="DateTimeKind.Unspecified"/> value is read as UTC
    /// rather than as machine-local time, which would make resolution depend on the server's time zone.
    /// </summary>
    public static DateTime ToDateTime(object value, int columnIndex)
    {
        if (value is not DateTime dateTimeValue)
        {
            throw Mismatch(value, columnIndex, "DateTime");
        }

        return dateTimeValue.Kind switch
        {
            DateTimeKind.Utc => dateTimeValue,
            DateTimeKind.Local => dateTimeValue.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Utc),
        };
    }

    public static TimeOnly ToTime(object value, int columnIndex) =>
        value switch
        {
            TimeOnly timeOnlyValue => timeOnlyValue,
            TimeSpan timeSpanValue => TimeOnly.FromTimeSpan(timeSpanValue),
            _ => throw Mismatch(value, columnIndex, "TimeOnly"),
        };

    private static InvalidOperationException Mismatch(object value, int columnIndex, string expectedType) =>
        new(
            $"Natural-key probe value at column {columnIndex} is a '{value.GetType().Name}' but the probe column "
                + $"requires a '{expectedType}'. Probe values must be converted with RelationalScalarLiteralParser "
                + "against the target column's scalar type."
        );
}
