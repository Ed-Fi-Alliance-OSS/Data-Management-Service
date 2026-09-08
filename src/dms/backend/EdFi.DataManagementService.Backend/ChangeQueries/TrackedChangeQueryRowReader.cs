// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.ChangeQueries;

internal static class TrackedChangeQueryRowReader
{
    public static async Task<TrackedChangeQueryResult> ReadAsync(
        IRelationalCommandReader reader,
        ChangeQueryEndpointOperation operation,
        IReadOnlyList<ChangeQueryResponseField> fields,
        bool includesTotalCount,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fields);

        long? totalCount = null;

        if (includesTotalCount)
        {
            totalCount = await ReadTotalCountAsync(reader, cancellationToken).ConfigureAwait(false);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        JsonArray items = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadRow(reader, operation, fields));
        }

        return new TrackedChangeQueryResult(items, totalCount);
    }

    private static async Task<long> ReadTotalCountAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0L;
        }

        var ordinal = reader.GetOrdinal("__TotalCount");
        return reader.IsDBNull(ordinal)
            ? 0L
            : Convert.ToInt64(reader.GetFieldValue<object>(ordinal), CultureInfo.InvariantCulture);
    }

    private static JsonObject ReadRow(
        IRelationalCommandReader reader,
        ChangeQueryEndpointOperation operation,
        IReadOnlyList<ChangeQueryResponseField> fields
    )
    {
        JsonObject item = new()
        {
            ["id"] = ReadId(reader),
            ["changeVersion"] = ReadLong(reader, "__ChangeVersion"),
        };

        switch (operation)
        {
            case ChangeQueryEndpointOperation.Deletes:
                item["keyValues"] = ReadKeyValues(reader, fields, useNewValues: false);
                break;

            case ChangeQueryEndpointOperation.KeyChanges:
                item["oldKeyValues"] = ReadKeyValues(reader, fields, useNewValues: false);
                item["newKeyValues"] = ReadKeyValues(reader, fields, useNewValues: true);
                break;

            default:
                throw new NotSupportedException(
                    $"Change query endpoint operation '{operation}' is not supported."
                );
        }

        return item;
    }

    private static JsonObject ReadKeyValues(
        IRelationalCommandReader reader,
        IReadOnlyList<ChangeQueryResponseField> fields,
        bool useNewValues
    )
    {
        JsonObject keyValues = [];
        var scalarOrDescriptorNamespaceSuffix = useNewValues ? "__new" : "__old";
        var descriptorCodeValueSuffix = useNewValues ? "__newCodeValue" : "__oldCodeValue";

        foreach (var field in fields)
        {
            keyValues[field.QueryFieldName] = field.Kind switch
            {
                ChangeQueryResponseFieldKind.Scalar => ReadScalarValue(
                    reader,
                    $"{field.QueryFieldName}{scalarOrDescriptorNamespaceSuffix}",
                    useNewValues ? field.NewColumn : field.OldColumn
                ),
                ChangeQueryResponseFieldKind.Descriptor => ReadDescriptorValue(
                    reader,
                    namespaceAlias: $"{field.QueryFieldName}{scalarOrDescriptorNamespaceSuffix}",
                    codeValueAlias: $"{field.QueryFieldName}{descriptorCodeValueSuffix}"
                ),
                _ => throw new NotSupportedException(
                    $"Change query response field kind '{field.Kind}' is not supported."
                ),
            };
        }

        return keyValues;
    }

    private static string ReadId(IRelationalCommandReader reader)
    {
        object value = ReadRequiredValue(reader, "__Id");

        return value switch
        {
            Guid guid => guid.ToString(),
            string text when Guid.TryParse(text, out var guid) => guid.ToString(),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("Column '__Id' did not contain a string value."),
        };
    }

    private static long ReadLong(IRelationalCommandReader reader, string alias)
    {
        object value = ReadRequiredValue(reader, alias);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static JsonNode? ReadDescriptorValue(
        IRelationalCommandReader reader,
        string namespaceAlias,
        string codeValueAlias
    )
    {
        object? namespaceValue = ReadNullableValue(reader, namespaceAlias);
        object? codeValue = ReadNullableValue(reader, codeValueAlias);

        if (namespaceValue is null || codeValue is null)
        {
            return null;
        }

        return JsonValue.Create(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Convert.ToString(namespaceValue, CultureInfo.InvariantCulture)}#{Convert.ToString(codeValue, CultureInfo.InvariantCulture)}"
            )
        );
    }

    private static JsonNode? ReadScalarValue(
        IRelationalCommandReader reader,
        string alias,
        TrackedChangeColumnInfo column
    )
    {
        object? value = ReadNullableValue(reader, alias);

        if (value is null)
        {
            return null;
        }

        JsonNode? scalarKindValue = TryReadScalarKindValue(value, column.ScalarType.Kind);

        if (scalarKindValue is not null)
        {
            return scalarKindValue;
        }

        return value switch
        {
            DateOnly dateOnly => JsonValue.Create(
                dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ),
            DateTime dateTime => JsonValue.Create(
                NormalizeUtcDateTime(dateTime).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            ),
            DateTimeOffset dateTimeOffset => JsonValue.Create(
                dateTimeOffset.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            ),
            TimeOnly timeOnly => JsonValue.Create(
                timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            ),
            Guid guid => JsonValue.Create(guid.ToString()),
            bool boolean => JsonValue.Create(boolean),
            int integer => JsonValue.Create(integer),
            long integer => JsonValue.Create(integer),
            decimal number => JsonValue.Create(number),
            string text => JsonValue.Create(text),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    private static JsonNode? TryReadScalarKindValue(object value, ScalarKind scalarKind) =>
        scalarKind switch
        {
            ScalarKind.Date => value switch
            {
                DateOnly dateOnly => JsonValue.Create(
                    dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ),
                DateTime dateTime => JsonValue.Create(
                    DateOnly.FromDateTime(dateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ),
                DateTimeOffset dateTimeOffset => JsonValue.Create(
                    DateOnly
                        .FromDateTime(dateTimeOffset.DateTime)
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ),
                _ => null,
            },
            ScalarKind.DateTime => value switch
            {
                DateTime dateTime => JsonValue.Create(
                    NormalizeUtcDateTime(dateTime)
                        .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                ),
                DateTimeOffset dateTimeOffset => JsonValue.Create(
                    dateTimeOffset.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                ),
                _ => null,
            },
            ScalarKind.Time => value switch
            {
                TimeOnly timeOnly => JsonValue.Create(
                    timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                ),
                TimeSpan timeSpan => JsonValue.Create(
                    TimeOnly.FromTimeSpan(timeSpan).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                ),
                DateTime dateTime => JsonValue.Create(
                    TimeOnly.FromDateTime(dateTime).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                ),
                DateTimeOffset dateTimeOffset => JsonValue.Create(
                    TimeOnly
                        .FromDateTime(dateTimeOffset.DateTime)
                        .ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                ),
                _ => null,
            },
            _ => null,
        };

    private static DateTime NormalizeUtcDateTime(DateTime dateTime) =>
        dateTime.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeKind.Utc => dateTime,
            _ => dateTime.ToUniversalTime(),
        };

    private static object ReadRequiredValue(IRelationalCommandReader reader, string alias)
    {
        var ordinal = reader.GetOrdinal(alias);

        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException($"Column '{alias}' does not contain a value.");
        }

        return reader.GetFieldValue<object>(ordinal);
    }

    private static object? ReadNullableValue(IRelationalCommandReader reader, string alias)
    {
        var ordinal = reader.GetOrdinal(alias);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<object>(ordinal);
    }
}
