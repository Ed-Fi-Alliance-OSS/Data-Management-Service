// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.Backend;

internal interface IDescriptorReadCandidateMetadata
{
    long DocumentId { get; }

    Guid DocumentUuid { get; }

    long ContentVersion { get; }

    DateTimeOffset ContentLastModifiedAt { get; }

    short ResourceKeyId { get; }

    string? Namespace { get; }

    string CodeValue { get; }

    string? Discriminator { get; }

    /// <summary>
    /// The <c>ContentVersion</c> page selection ordered this row by, projected out of the page-selection
    /// relation itself, or <see langword="null"/> when this row did not come from a
    /// <c>ContentVersion</c>-anchored page selection.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ContentVersion"/>, even though the two hold the same value for a
    /// committed row. <see cref="ContentVersion"/> is the canonical <c>dms.Document</c> value and is
    /// what the response body, the served ETag, and the cache-admission comparison are built from.
    /// This one is the root <c>dms.Descriptor</c> mirror that page selection actually ordered, bounded,
    /// and indexed on, and it is the only one that can anchor a continuation: the two are read from
    /// different tables in one statement, so under a provider that admits intra-statement read skew a
    /// concurrent update can make the document value the larger of the pair — and a continuation
    /// anchored on it would start the next page past rows this one never returned.
    /// </remarks>
    long? SelectedAnchor { get; }
}

internal sealed record DescriptorReadRow(
    long DocumentId,
    Guid DocumentUuid,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt,
    short ResourceKeyId,
    string? Namespace,
    string CodeValue,
    string ShortDescription,
    string? Description,
    DateOnly? EffectiveBeginDate,
    DateOnly? EffectiveEndDate,
    string? Discriminator,
    long? SelectedAnchor = null
) : IDescriptorReadCandidateMetadata;

internal sealed record DescriptorReadCandidateRow(
    long DocumentId,
    Guid DocumentUuid,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt,
    short ResourceKeyId,
    string? Namespace,
    string CodeValue,
    string? Discriminator,
    long? SelectedAnchor = null
) : IDescriptorReadCandidateMetadata;

internal sealed class DescriptorReadInvariantException(string message) : InvalidOperationException(message);

/// <summary>
/// Shared relational reader for descriptor rows emitted from <c>dms.Document</c> joined to
/// <c>dms.Descriptor</c>.
/// </summary>
internal static class DescriptorReadRowReader
{
    private const string DocumentIdColumnName = "DocumentId";
    private const string DocumentUuidColumnName = "DocumentUuid";
    private const string ContentVersionColumnName = "ContentVersion";
    private const string ContentLastModifiedAtColumnName = "ContentLastModifiedAt";
    private const string ResourceKeyIdColumnName = "ResourceKeyId";
    private const string NamespaceColumnName = "Namespace";
    private const string CodeValueColumnName = "CodeValue";
    private const string ShortDescriptionColumnName = "ShortDescription";
    private const string DescriptionColumnName = "Description";
    private const string EffectiveBeginDateColumnName = "EffectiveBeginDate";
    private const string EffectiveEndDateColumnName = "EffectiveEndDate";
    private const string DiscriminatorColumnName = "Discriminator";

    /// <summary>
    /// The alias the page-rows statement projects the page-selection anchor under. Deliberately not
    /// <c>ContentVersion</c>: that name is already taken by the canonical <c>dms.Document</c> column in
    /// the same projection, and the whole point of this column is that it comes from somewhere else.
    /// </summary>
    internal const string SelectedAnchorColumnName = "SelectedAnchor";

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

        // The GET-by-id statements project no page-selection relation, so there is no anchor to read.
        var row = ReadCurrentRow(reader, carriesSelectedAnchor: false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Descriptor single-row read returned multiple rows.");
        }

        return row;
    }

    /// <param name="carriesSelectedAnchor">
    /// Whether the statement feeding this reader projected the page-selection anchor. Supplied by the
    /// caller from the same predicate the projection was emitted under, rather than discovered from the
    /// result set: a page that carries no anchor must not pay to find that out, and a reader that
    /// answered the question its own way could disagree with the statement that produced the row.
    /// </param>
    public static async Task<IReadOnlyList<DescriptorReadRow>> ReadAllAsync(
        IRelationalCommandReader reader,
        bool carriesSelectedAnchor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        List<DescriptorReadRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadCurrentRow(reader, carriesSelectedAnchor));
        }

        return rows;
    }

    private static DescriptorReadRow ReadCurrentRow(
        IRelationalCommandReader reader,
        bool carriesSelectedAnchor
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        var documentId = reader.GetRequiredFieldValue<long>(DocumentIdColumnName);
        var documentUuid = reader.GetRequiredFieldValue<Guid>(DocumentUuidColumnName);
        var contentVersion = reader.GetRequiredFieldValue<long>(ContentVersionColumnName);
        var resourceKeyId = reader.GetRequiredFieldValue<short>(ResourceKeyIdColumnName);

        return new DescriptorReadRow(
            DocumentId: documentId,
            DocumentUuid: documentUuid,
            ContentVersion: contentVersion,
            ContentLastModifiedAt: ReadRequiredDateTimeOffsetFieldValue(
                reader,
                ContentLastModifiedAtColumnName,
                documentId,
                resourceKeyId
            ),
            ResourceKeyId: resourceKeyId,
            // Namespace is read nullably so a stored NULL flows into the namespace-authorization
            // stored-namespace-uninitialized 403 path instead of being masked as a 500 invariant
            // before the auth check runs. Callers that have no namespace authorization configured
            // must still treat a NULL value as corruption.
            Namespace: reader.GetNullableFieldValue<string>(NamespaceColumnName),
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
            Discriminator: ReadOptionalStringField(reader, DiscriminatorColumnName),
            SelectedAnchor: ReadSelectedAnchor(reader, carriesSelectedAnchor)
        );
    }

    public static async Task<DescriptorReadCandidateRow?> ReadSingleCandidateOrDefaultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The GET-by-id statements project no page-selection relation, so there is no anchor to read.
        var row = ReadCurrentCandidateRow(reader, carriesSelectedAnchor: false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Descriptor single-row read returned multiple rows.");
        }

        return row;
    }

    /// <param name="carriesSelectedAnchor">
    /// Whether the statement feeding this reader projected the page-selection anchor, supplied the same
    /// way and for the same reason as on <see cref="ReadAllAsync" />.
    /// </param>
    public static async Task<IReadOnlyList<DescriptorReadCandidateRow>> ReadAllCandidatesAsync(
        IRelationalCommandReader reader,
        bool carriesSelectedAnchor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        List<DescriptorReadCandidateRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadCurrentCandidateRow(reader, carriesSelectedAnchor));
        }

        return rows;
    }

    private static DescriptorReadCandidateRow ReadCurrentCandidateRow(
        IRelationalCommandReader reader,
        bool carriesSelectedAnchor
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        var documentId = reader.GetRequiredFieldValue<long>(DocumentIdColumnName);
        var documentUuid = reader.GetRequiredFieldValue<Guid>(DocumentUuidColumnName);
        var contentVersion = reader.GetRequiredFieldValue<long>(ContentVersionColumnName);
        var resourceKeyId = reader.GetRequiredFieldValue<short>(ResourceKeyIdColumnName);

        return new DescriptorReadCandidateRow(
            DocumentId: documentId,
            DocumentUuid: documentUuid,
            ContentVersion: contentVersion,
            ContentLastModifiedAt: ReadRequiredDateTimeOffsetFieldValue(
                reader,
                ContentLastModifiedAtColumnName,
                documentId,
                resourceKeyId
            ),
            ResourceKeyId: resourceKeyId,
            Namespace: reader.GetNullableFieldValue<string>(NamespaceColumnName),
            CodeValue: ReadRequiredDescriptorStringField(
                reader,
                CodeValueColumnName,
                documentId,
                resourceKeyId
            ),
            Discriminator: ReadOptionalStringField(reader, DiscriminatorColumnName),
            SelectedAnchor: ReadSelectedAnchor(reader, carriesSelectedAnchor)
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

    /// <summary>
    /// Reads the page-selection anchor, or <see langword="null"/> when this statement projected none.
    /// </summary>
    /// <remarks>
    /// Absence is decided by the caller rather than discovered here. The GET-by-id statements and the
    /// selected-page fallback have no page-selection relation to take an anchor from, and probing the
    /// result set for the column would make every row of every such page pay for a lookup that is
    /// certain to fail — which, on a reader whose absent-name signal is an exception, means a thrown
    /// and caught exception per row on the ordinary unwindowed descriptor page.
    /// </remarks>
    private static long? ReadSelectedAnchor(IRelationalCommandReader reader, bool carriesSelectedAnchor)
    {
        if (!carriesSelectedAnchor)
        {
            return null;
        }

        int ordinal;

        try
        {
            ordinal = reader.GetOrdinal(SelectedAnchorColumnName);
        }
        catch (IndexOutOfRangeException)
        {
            // A guard, not a probe: unreachable while the projection and this reader agree, so it costs
            // nothing on the anchored path. Reaching it means the page-rows SQL projected no anchor for
            // a request that resolved one. Reported as an absent anchor rather than thrown from inside
            // the row loop, so the boundary calculation raises the named disagreement it already has -
            // which is where a caller that requires the value says so.
            return null;
        }

        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<long>(ordinal);
    }

    private static string BuildRequiredDescriptorColumnNullMessage(
        string columnName,
        long documentId,
        short resourceKeyId
    ) =>
        $"Descriptor read corruption detected for DocumentId {documentId} (ResourceKeyId={resourceKeyId}): "
        + $"dms.Descriptor.{columnName} must not be null.";
}
