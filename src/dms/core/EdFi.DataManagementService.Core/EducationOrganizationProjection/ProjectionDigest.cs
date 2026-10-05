// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// </remarks>
internal static class ProjectionDigest
{
    /// <summary>The digest length in bytes.</summary>
    public const int ByteLength = 32;

    private const string HeaderLine = "edorg-projection-digest:v1\n";
    private const char FieldSeparator = ';';
    private const string NullField = "-";

    private static readonly UTF8Encoding _strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    /// <summary>
    /// Computes the digest of a set. Items are hashed in ascending id order whatever order they are
    /// supplied in.
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
        WriteCanonicalLines(
            items,
            line => hash.AppendData(_strictUtf8.GetBytes(line)),
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

        StringBuilder text = new();
        WriteCanonicalLines(items, line => text.Append(line), rowWritten: null);
        return text.ToString();
    }

    /// <summary>
    /// The single writer of the canonical form, shared by <see cref="Compute"/> and
    /// <see cref="CanonicalText"/> so the text a test pins is the text that is hashed.
    /// </summary>
    private static void WriteCanonicalLines(
        IReadOnlyCollection<ProjectionItem> items,
        Action<string> write,
        Action<int>? rowWritten
    )
    {
        write(HeaderLine);
        write(items.Count.ToString(CultureInfo.InvariantCulture) + "\n");

        int rowsWritten = 0;

        // A stable sort, so the text is fully determined by the set even before validation has
        // rejected duplicate ids.
        foreach (ProjectionItem item in items.OrderBy(item => item.EducationOrganizationId))
        {
            write(RowLine(item));
            rowWritten?.Invoke(++rowsWritten);
        }
    }

    private static string RowLine(ProjectionItem item) =>
        string.Concat(
            item.EducationOrganizationId.ToString(CultureInfo.InvariantCulture),
            FieldSeparator.ToString(),
            Tag(item.Kind),
            FieldSeparator.ToString(),
            LengthPrefixed(item.NameOfInstitution),
            FieldSeparator.ToString(),
            LengthPrefixed(item.ShortNameOfInstitution),
            FieldSeparator.ToString(),
            item.ParentId?.ToString(CultureInfo.InvariantCulture) ?? NullField,
            "\n"
        );

    /// <summary>
    /// <c>-</c> for null; otherwise the UTF-8 byte count, a colon and the value. A lone <c>-</c> never
    /// collides with a value, because every value carries a count.
    /// </summary>
    private static string LengthPrefixed(string? value) =>
        value is null
            ? NullField
            : _strictUtf8.GetByteCount(value).ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string Tag(ProjectionItemKind kind) =>
        kind switch
        {
            ProjectionItemKind.StateEducationAgency => "SEA",
            ProjectionItemKind.EducationServiceCenter => "ESC",
            ProjectionItemKind.LocalEducationAgency => "LEA",
            ProjectionItemKind.School => "SCH",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported projection kind."),
        };
}
