// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// The SHA-256 digest of a projected set's canonical text, which a walk's cursor carries so a later
/// page is answered only from a set with the same projected content.
/// </summary>
/// <remarks>
/// The canonical text is pinned by the contract (<c>docs/EDUCATION-ORGANIZATION-PROJECTION.md</c>,
/// "Canonical digest form"): UTF-8 without a byte order mark, LF line endings, a header line, the row
/// count, then one line per item in ascending id order. Ids, parent ids and the count are
/// invariant-culture decimals, so a negative id keeps its leading <c>-</c> and nothing else varies
/// with culture. Each string is length-prefixed with its UTF-8 byte count, which keeps <c>;</c>,
/// <c>:</c> and line breaks inside names unambiguous. Changing any of this changes every digest, so
/// every cursor in flight would answer <c>projection-changed</c> once; a format change needs a new
/// header version.
/// <para>
/// Encoding is strict: a string that is not well-formed UTF-16 (a lone high or low surrogate) throws
/// <see cref="EncoderFallbackException"/> rather than being replaced by U+FFFD. Replacement would give
/// different malformed names identical canonical bytes, so a change between them would not change
/// the digest and a walk could mix two states without a SHA-256 collision. Set validation rejects
/// malformed names as <c>projection-data-invalid</c> before hashing; the exception is the backstop
/// should one ever reach this class.
/// </para>
/// <para>
/// The canonical bytes are formatted straight into one pooled buffer, a line at a time, and fed to
/// the hash; no string or array is built per row. Every page of a walk hashes the whole set, so a
/// per-row allocation would be repeated for every row of every page.
/// </para>
/// </remarks>
internal static class ProjectionDigest
{
    /// <summary>The digest length in bytes.</summary>
    public const int ByteLength = 32;

    /// <summary>Enough for a row whose names are each well within the 75-character data bound.</summary>
    private const int InitialBufferBytes = 1024;

    /// <summary>The longest invariant decimal of an int64: <c>-9223372036854775808</c>.</summary>
    private const int MaxInt64Bytes = 20;

    /// <summary>The longest invariant decimal of an int32 byte count.</summary>
    private const int MaxInt32Bytes = 11;

    /// <summary>A UTF-16 code unit is at most three UTF-8 bytes; a surrogate pair is four for two.</summary>
    private const int MaxUtf8BytesPerChar = 3;

    private const byte FieldSeparator = (byte)';';
    private const byte LengthSeparator = (byte)':';
    private const byte NullField = (byte)'-';
    private const byte LineFeed = (byte)'\n';

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>Receives the canonical bytes in order. The span is valid only during the call.</summary>
    private delegate void CanonicalBytesSink(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Computes the digest of a set. Items are hashed in ascending id order whatever order they are
    /// supplied in (see <see cref="InAscendingIdOrder"/>).
    /// </summary>
    /// <exception cref="EncoderFallbackException">A name is not well-formed UTF-16.</exception>
    public static byte[] Compute(IReadOnlyCollection<ProjectionItem> items) =>
        Compute(items, NoOpProjectionProcessingObserver.Instance, CancellationToken.None);

    /// <summary>
    /// Computes the digest of a set, reporting a <see cref="ProjectionProcessingStage.Hashing"/>
    /// checkpoint and checking for cancellation every
    /// <see cref="ProjectionProcessing.CheckpointInterval"/> rows.
    /// </summary>
    /// <exception cref="EncoderFallbackException">A name is not well-formed UTF-16.</exception>
    public static byte[] Compute(
        IReadOnlyCollection<ProjectionItem> items,
        IProjectionProcessingObserver observer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(observer);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        WriteCanonicalBytes(
            items,
            bytes => hash.AppendData(bytes),
            rowsWritten =>
                ProjectionProcessing.Checkpoint(
                    observer,
                    ProjectionProcessingStage.Hashing,
                    rowsWritten,
                    cancellationToken
                )
        );
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// The digest in the form a cursor carries it: unpadded base64url.
    /// </summary>
    public static string ToCursorField(byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (digest.Length != ByteLength)
        {
            throw new ArgumentException($"A digest is {ByteLength} bytes.", nameof(digest));
        }

        return Base64Url.EncodeToString(digest);
    }

    /// <summary>
    /// The exact text <see cref="Compute"/> hashes, for golden-vector tests and diagnostics.
    /// </summary>
    internal static string CanonicalText(IReadOnlyCollection<ProjectionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        ArrayBufferWriter<byte> text = new();
        WriteCanonicalBytes(items, bytes => text.Write(bytes), rowWritten: null);
        return _strictUtf8.GetString(text.WrittenSpan);
    }

    /// <summary>
    /// The order items are hashed in: ascending id, and for equal ids the order they were supplied in.
    /// </summary>
    /// <remarks>
    /// This is a stable sort by id. A sequence already in non-decreasing id order, which is what the
    /// handler supplies, is returned as supplied without sorting: a stable sort would leave it exactly
    /// as it is, so the bytes hashed are the same either way. Any other sequence is sorted.
    /// </remarks>
    internal static IEnumerable<ProjectionItem> InAscendingIdOrder(IReadOnlyCollection<ProjectionItem> items)
    {
        long? previous = null;

        foreach (long id in items.Select(item => item.EducationOrganizationId))
        {
            if (id < previous)
            {
                return items.OrderBy(item => item.EducationOrganizationId);
            }

            previous = id;
        }

        return items;
    }

    /// <summary>
    /// The single writer of the canonical form, shared by <see cref="Compute"/> and
    /// <see cref="CanonicalText"/> so the text a test pins is the text that is hashed.
    /// </summary>
    private static void WriteCanonicalBytes(
        IReadOnlyCollection<ProjectionItem> items,
        CanonicalBytesSink write,
        Action<int>? rowWritten
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferBytes);

        try
        {
            write("edorg-projection-digest:v1\n"u8);

            int countLength = WriteInt64(items.Count, buffer);
            buffer[countLength] = LineFeed;
            write(buffer.AsSpan(0, countLength + 1));

            int rowsWritten = 0;

            foreach (ProjectionItem item in InAscendingIdOrder(items))
            {
                int bound = MaxRowBytes(item);

                if (buffer.Length < bound)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(bound);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                write(buffer.AsSpan(0, WriteRow(item, buffer)));
                rowWritten?.Invoke(++rowsWritten);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes <c>&lt;id&gt;;&lt;tag&gt;;&lt;name&gt;;&lt;short name&gt;;&lt;parent id&gt;\n</c> and returns its
    /// length. The fields are written in order, so a bad kind is reported before a malformed name.
    /// </summary>
    private static int WriteRow(ProjectionItem item, Span<byte> destination)
    {
        int length = WriteInt64(item.EducationOrganizationId, destination);
        destination[length++] = FieldSeparator;

        ReadOnlySpan<byte> tag = Tag(item.Kind);
        tag.CopyTo(destination[length..]);
        length += tag.Length;
        destination[length++] = FieldSeparator;

        length += WriteLengthPrefixed(item.NameOfInstitution, destination[length..]);
        destination[length++] = FieldSeparator;

        length += WriteLengthPrefixed(item.ShortNameOfInstitution, destination[length..]);
        destination[length++] = FieldSeparator;

        if (item.ParentId is long parentId)
        {
            length += WriteInt64(parentId, destination[length..]);
        }
        else
        {
            destination[length++] = NullField;
        }

        destination[length++] = LineFeed;
        return length;
    }

    /// <summary>
    /// <c>-</c> for null; otherwise the UTF-8 byte count, a colon and the value. A lone <c>-</c> never
    /// collides with a value, because every value carries a count.
    /// </summary>
    /// <exception cref="EncoderFallbackException">The value is not well-formed UTF-16.</exception>
    private static int WriteLengthPrefixed(string? value, Span<byte> destination)
    {
        if (value is null)
        {
            destination[0] = NullField;
            return 1;
        }

        int byteCount = _strictUtf8.GetByteCount(value);
        int length = WriteInt64(byteCount, destination);
        destination[length++] = LengthSeparator;
        length += _strictUtf8.GetBytes(value, destination[length..]);
        return length;
    }

    /// <summary>The invariant-culture decimal: a leading <c>-</c> when negative, nothing else.</summary>
    private static int WriteInt64(long value, Span<byte> destination)
    {
        if (!value.TryFormat(destination, out int written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("The canonical row buffer is too small.");
        }

        return written;
    }

    /// <summary>A bound on the bytes <see cref="WriteRow"/> writes for an item.</summary>
    private static int MaxRowBytes(ProjectionItem item) =>
        MaxInt64Bytes
        + 1
        + 3
        + 1
        + MaxLengthPrefixedBytes(item.NameOfInstitution)
        + 1
        + MaxLengthPrefixedBytes(item.ShortNameOfInstitution)
        + 1
        + MaxInt64Bytes
        + 1;

    private static int MaxLengthPrefixedBytes(string? value) =>
        value is null ? 1 : MaxInt32Bytes + 1 + (value.Length * MaxUtf8BytesPerChar);

    private static ReadOnlySpan<byte> Tag(ProjectionItemKind kind) =>
        kind switch
        {
            ProjectionItemKind.StateEducationAgency => "SEA"u8,
            ProjectionItemKind.EducationServiceCenter => "ESC"u8,
            ProjectionItemKind.LocalEducationAgency => "LEA"u8,
            ProjectionItemKind.School => "SCH"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported projection kind."),
        };
}
