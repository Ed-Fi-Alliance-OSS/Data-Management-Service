// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Globalization;
using System.Text;
using Code = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;

/// <summary>The outcome of resolving one Discovery URL template.</summary>
public abstract record ProjectionUrlResolution
{
    private ProjectionUrlResolution() { }

    /// <summary>The URL to call: same origin as the base URL and under its path.</summary>
    public sealed record Resolved(Uri Url) : ProjectionUrlResolution;

    /// <summary><c>DiscoveryInvalid</c> or <c>TargetNotRoutable</c>; nothing may be requested.</summary>
    public sealed record Rejected(Code Code) : ProjectionUrlResolution;
}

/// <summary>
/// Resolves a Discovery URL template, <c>urls.oauth</c> or <c>urls.educationOrganizationProjection</c>, for one data
/// store (DMS-1440 spec §5.3 items 4 and 5, D-14). The checks run in this order, and the first failure decides:
/// <list type="number">
/// <item>Shape: an absolute URL with no query, fragment or backslash and no placeholder outside its path, whose scheme,
/// host and port equal the base URL's (default ports elided, scheme and host in any letter case) and which carries no
/// user information; balanced, non-empty <c>{name}</c> placeholders; no literal dot segment, including a
/// percent-encoded one. Otherwise <c>DiscoveryInvalid</c>.</item>
/// <item>Substitution: each <c>{name}</c> becomes the store context whose key equals <c>name</c> (exactly, else the one
/// value of the keys equal ignoring case), percent-encoded with <see cref="Uri.EscapeDataString(string)"/>. A missing,
/// ambiguous, empty or malformed UTF-16 value, a <c>{tenant}</c> placeholder (the tenant comes from the Discovery path,
/// never from a store context), or a segment the values turn into <c>.</c> or <c>..</c> is
/// <c>TargetNotRoutable</c>.</item>
/// <item>Containment, on the URL as <see cref="Uri"/> parses it: same origin again, and the base URL's path segments a
/// prefix of the result's at segment boundaries (<c>/api</c> admits <c>/api/x</c>, not <c>/api-other</c>), comparing
/// segments case-sensitively after decoding unreserved percent-escapes and upper-casing the others. Otherwise
/// <c>DiscoveryInvalid</c>.</item>
/// </list>
/// The dot-segment checks run before <see cref="Uri"/> parses the result, because it decodes <c>%2E</c> and then removes
/// dot segments, which would turn <c>/api/%2E%2E/x</c> into <c>/x</c>.
/// </summary>
public static class ProjectionUrlTemplateResolver
{
    /// <summary>The placeholder that is never filled from a store context.</summary>
    public const string TenantPlaceholder = "tenant";

    private const char PathSeparator = '/';

    private static readonly ProjectionUrlResolution _discoveryInvalid = new ProjectionUrlResolution.Rejected(
        Code.DiscoveryInvalid
    );

    private static readonly ProjectionUrlResolution _notRoutable = new ProjectionUrlResolution.Rejected(
        Code.TargetNotRoutable
    );

    /// <summary>One piece of a path segment: literal template text or a placeholder name.</summary>
    private readonly record struct Part(bool IsPlaceholder, string Text);

    public static ProjectionUrlResolution Resolve(
        string template,
        IReadOnlyDictionary<string, string> dataStoreContexts,
        Uri baseUrl
    )
    {
        if (template.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            return _discoveryInvalid;
        }

        int schemeEnd = template.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return _discoveryInvalid;
        }

        int pathStart = template.IndexOf('/', schemeEnd + 3);
        string origin = pathStart < 0 ? template : template[..pathStart];
        string path = pathStart < 0 ? "/" : template[pathStart..];

        // A placeholder in the host or port does not parse, and one in user information fails IsSameOrigin.
        if (
            !Uri.TryCreate(origin + PathSeparator, UriKind.Absolute, out Uri? originUrl)
            || !IsSameOrigin(originUrl, baseUrl)
        )
        {
            return _discoveryInvalid;
        }

        // The path starts with '/', so the first element is empty and is not a segment.
        List<List<Part>> segments = [];
        foreach (string segment in path.Split('/').Skip(1))
        {
            List<Part>? parts = ParseSegment(segment);
            if (parts is null)
            {
                return _discoveryInvalid;
            }
            if (parts.TrueForAll(part => !part.IsPlaceholder) && IsDotSegment(segment))
            {
                return _discoveryInvalid;
            }
            segments.Add(parts);
        }

        StringBuilder resolvedPath = new();
        foreach (List<Part> parts in segments)
        {
            StringBuilder segment = new();
            foreach (Part part in parts)
            {
                if (!part.IsPlaceholder)
                {
                    segment.Append(part.Text);
                    continue;
                }

                string? value = ContextValue(dataStoreContexts, part.Text);
                if (value is null)
                {
                    return _notRoutable;
                }
                segment.Append(Uri.EscapeDataString(value));
            }

            if (IsDotSegment(segment.ToString()))
            {
                return _notRoutable;
            }
            resolvedPath.Append('/').Append(segment);
        }

        if (
            !Uri.TryCreate(origin + resolvedPath, UriKind.Absolute, out Uri? resolved)
            || !IsSameOrigin(resolved, baseUrl)
            || resolved.Query.Length > 0
            || resolved.Fragment.Length > 0
            || !IsUnderBasePath(resolved, baseUrl)
        )
        {
            return _discoveryInvalid;
        }

        return new ProjectionUrlResolution.Resolved(resolved);
    }

    /// <summary>
    /// Scheme, host and port equal (<see cref="Uri"/> lower-cases the scheme and host and supplies default ports),
    /// and no user information.
    /// </summary>
    internal static bool IsSameOrigin(Uri candidate, Uri baseUrl) =>
        string.Equals(candidate.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.IdnHost, baseUrl.IdnHost, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == baseUrl.Port
        && candidate.UserInfo.Length == 0;

    /// <summary>
    /// The literal and placeholder parts of one segment, or <c>null</c> for an unbalanced, nested or empty placeholder.
    /// </summary>
    private static List<Part>? ParseSegment(string segment)
    {
        List<Part> parts = [];
        int position = 0;
        while (position < segment.Length)
        {
            int open = segment.IndexOf('{', position);
            int close = segment.IndexOf('}', position);
            if (open < 0)
            {
                if (close >= 0)
                {
                    return null;
                }
                parts.Add(new Part(false, segment[position..]));
                break;
            }
            if (close >= 0 && close < open)
            {
                return null;
            }

            close = segment.IndexOf('}', open + 1);
            if (close < 0)
            {
                return null;
            }

            string name = segment[(open + 1)..close];
            if (name.Length == 0 || name.Contains('{'))
            {
                return null;
            }

            if (open > position)
            {
                parts.Add(new Part(false, segment[position..open]));
            }
            parts.Add(new Part(true, name));
            position = close + 1;
        }
        return parts;
    }

    /// <summary>
    /// The value to substitute: the context whose key equals <paramref name="name"/> exactly, else the one value shared
    /// by the keys equal to it ignoring case. <c>null</c> for <see cref="TenantPlaceholder"/>, and for a missing,
    /// ambiguous, empty or malformed UTF-16 value, which <see cref="Uri.EscapeDataString(string)"/> would otherwise
    /// replace with U+FFFD and so route somewhere else.
    /// </summary>
    private static string? ContextValue(IReadOnlyDictionary<string, string> dataStoreContexts, string name)
    {
        if (string.Equals(name, TenantPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? match = null;
        bool ambiguous = false;
        foreach ((string key, string value) in dataStoreContexts)
        {
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                match = value;
                ambiguous = false;
                break;
            }
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                ambiguous |= match is not null && !string.Equals(match, value, StringComparison.Ordinal);
                match ??= value;
            }
        }

        return ambiguous || string.IsNullOrEmpty(match) || !IsWellFormedUtf16(match) ? null : match;
    }

    /// <summary>Whether every surrogate in <paramref name="value"/> is part of a pair.</summary>
    internal static bool IsWellFormedUtf16(string value)
    {
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out int consumed) != OperationStatus.Done)
            {
                return false;
            }
            remaining = remaining[consumed..];
        }
        return true;
    }

    /// <summary>Whether a raw segment is <c>.</c> or <c>..</c> once unreserved percent-escapes are decoded.</summary>
    private static bool IsDotSegment(string segment) => Normalize(segment) is "." or "..";

    private static bool IsUnderBasePath(Uri resolved, Uri baseUrl)
    {
        string[] baseSegments = Segments(baseUrl.AbsolutePath);
        if (baseSegments.Length > 0 && baseSegments[^1].Length == 0)
        {
            // "/api/" and "/api" are the same base.
            baseSegments = baseSegments[..^1];
        }

        string[] resolvedSegments = Segments(resolved.AbsolutePath);
        if (resolvedSegments.Length < baseSegments.Length)
        {
            return false;
        }

        for (int index = 0; index < resolvedSegments.Length; index++)
        {
            if (resolvedSegments[index] is "." or "..")
            {
                return false;
            }
            if (index < baseSegments.Length && resolvedSegments[index] != baseSegments[index])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The normalized segments of an absolute path; "/" has one empty segment.</summary>
    private static string[] Segments(string absolutePath) =>
        [.. absolutePath.Split('/').Skip(1).Select(Normalize)];

    /// <summary>
    /// Decodes percent-escapes of unreserved characters (RFC 3986 §2.3) and upper-cases the hex digits of the rest,
    /// so equivalent spellings of a segment compare equal.
    /// </summary>
    private static string Normalize(string segment)
    {
        if (!segment.Contains('%'))
        {
            return segment;
        }

        StringBuilder normalized = new(segment.Length);
        int index = 0;
        while (index < segment.Length)
        {
            if (
                segment[index] == '%'
                && index + 2 < segment.Length
                && byte.TryParse(
                    segment.AsSpan(index + 1, 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out byte value
                )
            )
            {
                char decoded = (char)value;
                if (char.IsAsciiLetterOrDigit(decoded) || decoded is '-' or '.' or '_' or '~')
                {
                    normalized.Append(decoded);
                }
                else
                {
                    normalized.Append('%').Append(segment.AsSpan(index + 1, 2).ToString().ToUpperInvariant());
                }
                index += 3;
            }
            else
            {
                normalized.Append(segment[index]);
                index++;
            }
        }
        return normalized.ToString();
    }
}
