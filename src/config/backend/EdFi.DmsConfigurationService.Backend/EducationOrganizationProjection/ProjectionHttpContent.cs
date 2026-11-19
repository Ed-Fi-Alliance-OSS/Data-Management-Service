// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>The raw <c>type</c> and <c>correlationId</c> of a DMS problem document, either possibly absent.</summary>
internal readonly record struct ProblemFields(string? Type, string? CorrelationId)
{
    public static ProblemFields None { get; } = new(null, null);
}

/// <summary>Bounded reads of DMS response bodies for the projection reader (DMS-1440 spec §5.4, §5.5).</summary>
internal static class ProjectionHttpContent
{
    /// <summary>The largest problem body read for its <c>type</c> (§5.5); a longer one has no type.</summary>
    public const int MaxProblemBodyBytes = 65_536;

    /// <summary>The header DMS token endpoint errors carry their correlation id in, percent-encoded.</summary>
    public const string CorrelationIdHeaderName = "X-Correlation-ID";

    /// <summary>
    /// The longest raw <see cref="CorrelationIdHeaderName"/> value decoded: a value of
    /// <see cref="EducationOrganizationProjectionFailure.MaxCorrelationIdLength"/> characters, each percent-encoded.
    /// </summary>
    public const int MaxCorrelationIdHeaderLength =
        3 * EducationOrganizationProjectionFailure.MaxCorrelationIdLength;

    private const int ChunkBytes = 16_384;

    /// <summary>
    /// The whole body, or <c>null</c> when it is longer than <paramref name="maxBytes"/>. A declared length over the
    /// cap is refused without reading; otherwise reading stops at the first byte past the cap.
    /// </summary>
    public static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken
    )
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream body = new();
        byte[] chunk = new byte[ChunkBytes];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return body.ToArray();
            }
            if (body.Length + read > maxBytes)
            {
                return null;
            }
            await body.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    /// <summary>
    /// The problem fields of an <c>application/problem+json</c> body of at most <see cref="MaxProblemBodyBytes"/>;
    /// any other content type, a longer body, or a body that is not a JSON object gives <see cref="ProblemFields.None"/>,
    /// and a member that is not a string is absent. Transport failures while reading propagate.
    /// </summary>
    public static async Task<ProblemFields> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        if (
            !string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/problem+json",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return ProblemFields.None;
        }

        byte[]? body = await ReadBoundedAsync(response.Content, MaxProblemBodyBytes, cancellationToken);
        if (body is null)
        {
            return ProblemFields.None;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ProblemFields.None;
            }
            return new ProblemFields(
                StringMember(document.RootElement, "type"),
                StringMember(document.RootElement, "correlationId")
            );
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Malformed JSON or invalid UTF-8 inside a string: an unparsable problem body has no type.
            return ProblemFields.None;
        }
    }

    private static string? StringMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The single <see cref="CorrelationIdHeaderName"/> value of <paramref name="response"/>, percent-decoded once as
    /// UTF-8, the inverse of the <see cref="Uri.EscapeDataString(string)"/> DMS writes it with. <c>null</c> when the
    /// header is absent or repeated, is longer than <see cref="MaxCorrelationIdHeaderLength"/>, holds a character
    /// outside visible ASCII or a <c>%</c> not followed by two hex digits, decodes to bytes that are not UTF-8, or
    /// decodes to more than <see cref="EducationOrganizationProjectionFailure.MaxCorrelationIdLength"/> characters.
    /// Never throws; the failure that carries the value sanitizes it.
    /// </summary>
    public static string? ReadCorrelationIdHeader(HttpResponseMessage response)
    {
        if (
            !response.Headers.NonValidated.TryGetValues(
                CorrelationIdHeaderName,
                out HeaderStringValues values
            )
            || values.Count != 1
        )
        {
            return null;
        }

        string raw = values.ToString();
        if (raw.Length > MaxCorrelationIdHeaderLength)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[MaxCorrelationIdHeaderLength];
        int length = 0;
        int index = 0;
        while (index < raw.Length)
        {
            char character = raw[index];
            if (character == '%')
            {
                if (
                    index + 2 >= raw.Length
                    || !byte.TryParse(
                        raw.AsSpan(index + 1, 2),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture,
                        out byte octet
                    )
                )
                {
                    return null;
                }
                bytes[length++] = octet;
                index += 3;
            }
            else if (character is > ' ' and < '\u007F')
            {
                bytes[length++] = (byte)character;
                index++;
            }
            else
            {
                return null;
            }
        }

        ReadOnlySpan<byte> decoded = bytes[..length];
        if (!Utf8.IsValid(decoded))
        {
            return null;
        }

        string value = Encoding.UTF8.GetString(decoded);
        return value.Length > EducationOrganizationProjectionFailure.MaxCorrelationIdLength ? null : value;
    }
}
