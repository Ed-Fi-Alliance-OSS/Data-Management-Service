// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend;

internal sealed record DescriptorReadRow(
    long DocumentId,
    Guid DocumentUuid,
    DateTimeOffset ContentLastModifiedAt,
    short ResourceKeyId,
    string Namespace,
    string CodeValue,
    string ShortDescription,
    string? Description,
    DateOnly? EffectiveBeginDate,
    DateOnly? EffectiveEndDate,
    string? Discriminator
);

internal sealed class DescriptorReadInvariantException(string message) : InvalidOperationException(message);

/// <summary>
/// Shared relational reader for descriptor rows emitted from <c>dms.Document</c> joined to
/// <c>dms.Descriptor</c>.
/// </summary>
internal static class DescriptorReadRowReader
{
    private const string DocumentIdColumnName = "DocumentId";
    private const string DocumentUuidColumnName = "DocumentUuid";
    private const string ContentLastModifiedAtColumnName = "ContentLastModifiedAt";
    private const string ResourceKeyIdColumnName = "ResourceKeyId";
    private const string NamespaceColumnName = "Namespace";
    private const string CodeValueColumnName = "CodeValue";
    private const string ShortDescriptionColumnName = "ShortDescription";
    private const string DescriptionColumnName = "Description";
    private const string EffectiveBeginDateColumnName = "EffectiveBeginDate";
    private const string EffectiveEndDateColumnName = "EffectiveEndDate";
    private const string DiscriminatorColumnName = "Discriminator";

    public static async Task<DescriptorReadRow?> ReadSingleOrDefaultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = ReadCurrentRow(reader);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Descriptor single-row read returned multiple rows.");
        }

        return row;
    }

    public static async Task<IReadOnlyList<DescriptorReadRow>> ReadAllAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        List<DescriptorReadRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadCurrentRow(reader));
        }

        return rows;
    }

    private static DescriptorReadRow ReadCurrentRow(IRelationalCommandReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var documentId = reader.GetRequiredFieldValue<long>(DocumentIdColumnName);
        var documentUuid = reader.GetRequiredFieldValue<Guid>(DocumentUuidColumnName);
        var resourceKeyId = reader.GetRequiredFieldValue<short>(ResourceKeyIdColumnName);

        return new DescriptorReadRow(
            DocumentId: documentId,
            DocumentUuid: documentUuid,
            ContentLastModifiedAt: ReadRequiredDateTimeOffsetFieldValue(
                reader,
                ContentLastModifiedAtColumnName,
                documentId,
                resourceKeyId
            ),
            ResourceKeyId: resourceKeyId,
            Namespace: ReadRequiredDescriptorStringField(
                reader,
                NamespaceColumnName,
                documentId,
                resourceKeyId
            ),
            CodeValue: ReadRequiredDescriptorStringField(
                reader,
                CodeValueColumnName,
                documentId,
                resourceKeyId
            ),
            ShortDescription: ReadRequiredDescriptorStringField(
                reader,
                ShortDescriptionColumnName,
                documentId,
                resourceKeyId
            ),
            Description: reader.GetNullableFieldValue<string>(DescriptionColumnName),
            EffectiveBeginDate: reader.GetNullableDateFieldValue(EffectiveBeginDateColumnName),
            EffectiveEndDate: reader.GetNullableDateFieldValue(EffectiveEndDateColumnName),
            Discriminator: ReadOptionalStringField(reader, DiscriminatorColumnName)
        );
    }

    private static string ReadRequiredDescriptorStringField(
        IRelationalCommandReader reader,
        string columnName,
        long documentId,
        short resourceKeyId
    )
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            throw new DescriptorReadInvariantException(
                BuildRequiredDescriptorColumnNullMessage(columnName, documentId, resourceKeyId)
            );
        }

        return reader.GetFieldValue<string>(ordinal);
    }

    private static DateTimeOffset ReadRequiredDateTimeOffsetFieldValue(
        IRelationalCommandReader reader,
        string columnName,
        long documentId,
        short resourceKeyId
    )
    {
        var ordinal = reader.GetOrdinal(columnName);

        if (reader.IsDBNull(ordinal))
        {
            throw new DescriptorReadInvariantException(
                $"Descriptor read corruption detected for DocumentId {documentId} (ResourceKeyId={resourceKeyId}): "
                    + $"dms.Document.{columnName} must not be null."
            );
        }

        var value = reader.GetFieldValue<object>(ordinal);

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Descriptor read expected a DateTimeOffset-compatible value for dms.Document.{columnName}, "
                    + $"but received '{value.GetType().Name}'."
            ),
        };
    }

    private static string? ReadOptionalStringField(IRelationalCommandReader reader, string columnName)
    {
        int ordinal;

        try
        {
            ordinal = reader.GetOrdinal(columnName);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }

        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string>(ordinal);
    }

    private static string BuildRequiredDescriptorColumnNullMessage(
        string columnName,
        long documentId,
        short resourceKeyId
    ) =>
        $"Descriptor read corruption detected for DocumentId {documentId} (ResourceKeyId={resourceKeyId}): "
        + $"dms.Descriptor.{columnName} must not be null.";
}
