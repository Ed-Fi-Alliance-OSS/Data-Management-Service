// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Centralized backend helper for building dictionary/HashSet keys from
/// <see cref="SemanticIdentityPart"/> sequences. The encoded key includes each part's
/// <see cref="SemanticIdentityPart.RelativePath"/>, <see cref="SemanticIdentityPart.IsPresent"/>,
/// and canonical serialized JSON value, so two identity sequences produce equal keys iff
/// they are structurally equal under
/// <see cref="ScopeInstanceAddressComparer.SemanticIdentityEquals"/>.
/// </summary>
/// <remarks>
/// Replaces three previously-duplicated private helpers in <c>ProfileCollectionPlanner</c>,
/// <c>ProfileCollectionWalker</c>, and <c>ProfileCollectionRowHiddenPathExpander</c> that
/// keyed on serialized value alone. The earlier shape collapsed missing identity parts
/// (<c>IsPresent: false, Value: null</c>) and explicit JSON nulls
/// (<c>IsPresent: true, Value: null</c>) onto the same <c>"null"</c> key, contradicting
/// the missing-vs-explicit-null guarantee that the Core <see cref="SemanticIdentityPart"/>
/// contract documents and that <see cref="ScopeInstanceAddressComparer"/> enforces.
/// </remarks>
internal static class SemanticIdentityKeys
{
    // Control-character separators chosen so they can appear neither in compiled
    // RelativePath strings (alphanum, dot, bracket, dollar, asterisk, underscore) nor in
    // JSON ToJsonString() output for legal scalar values (which would escape such bytes
    // inside string literals). Using control characters keeps the key unambiguous without
    // forcing an escape pass for every part.
    private const char PartSeparator = '\u001E'; // RS — between parts
    private const char FieldSeparator = '\u001F'; // US — within a part
    private const string NullValueSentinel = "null";

    /// <summary>
    /// Builds a dictionary key from an ordered sequence of <see cref="SemanticIdentityPart"/>
    /// values. Each part contributes <c>RelativePath</c>, <c>IsPresent</c>, and the canonical
    /// <c>ToJsonString</c> form of <c>Value</c> (or <c>"null"</c> when the value is null).
    /// </summary>
    public static string BuildKey(ImmutableArray<SemanticIdentityPart> parts)
    {
        if (parts.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(PartSeparator);
            }

            var part = parts[i];
            builder.Append(part.RelativePath);
            builder.Append(FieldSeparator);
            builder.Append(part.IsPresent ? '1' : '0');
            builder.Append(FieldSeparator);
            builder.Append(part.Value?.ToJsonString() ?? NullValueSentinel);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a dictionary key from a <see cref="CollectionWriteCandidate"/> by delegating to
    /// the part-array overload using <see cref="CollectionWriteCandidate.SemanticIdentityInOrder"/>.
    /// </summary>
    public static string BuildKey(CollectionWriteCandidate candidate) =>
        BuildKey(candidate.SemanticIdentityInOrder);

    /// <summary>
    /// Formats a <see cref="SemanticIdentityPart"/> sequence for human-readable diagnostic
    /// messages as <c>path=value</c> pairs joined by commas, with explicit <c>(missing)</c>
    /// annotation for parts whose <see cref="SemanticIdentityPart.IsPresent"/> is false. The
    /// <see cref="SemanticIdentityPart.RelativePath"/> values come from compiled metadata and
    /// must be wrapped in <c>LogSanitizer.SanitizeForLog</c> by callers that include the
    /// formatted output in log or exception messages.
    /// </summary>
    public static string FormatForDiagnostics(ImmutableArray<SemanticIdentityPart> parts)
    {
        if (parts.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var part = parts[i];
            builder.Append(part.RelativePath);
            builder.Append('=');
            builder.Append(part.Value?.ToJsonString() ?? NullValueSentinel);
            if (!part.IsPresent)
            {
                builder.Append("(missing)");
            }
        }

        return builder.ToString();
    }
}
