// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Classification result for a <c>CollectionExtensionScope</c> JsonScope canonical string.
/// <see cref="ParentCollectionScope"/> is the underlying collection scope the extension
/// aligns to. <see cref="IsMirrored"/> is <c>true</c> when the scope uses the mirrored
/// shape <c>"$._ext.&lt;extensionName&gt;.&lt;parentRemainder&gt;._ext.&lt;extensionName&gt;"</c>
/// with matching leading and trailing extension names — that is the only shape whose
/// parent collection re-roots under the document root rather than under the
/// extension-prefixed scope.
/// </summary>
internal readonly record struct AlignedExtensionScopeShape(string ParentCollectionScope, bool IsMirrored);

/// <summary>
/// Single source of truth for classifying a collection-aligned extension scope into its
/// underlying parent collection scope and whether the alignment is mirrored. The
/// flattener uses this to decide where to attach <c>AttachedAlignedScopeData</c>; the
/// profile walker uses it to dispatch aligned-extension scopes from the correct parent
/// collection candidate. Keeping the classification in one place prevents the two
/// callers from drifting apart on the strict shape contract — in particular, the
/// mirrored shape requires the leading and trailing <c>_ext.&lt;name&gt;</c> markers to
/// share the same extension name.
/// </summary>
internal static class AlignedExtensionScopeSupport
{
    private const string TrailingMarker = "._ext.";
    private const string MirroredRootPrefix = "$._ext.";

    /// <summary>
    /// Classifies <paramref name="alignedScope"/> as either a standard aligned scope
    /// (<c>"$.&lt;parent&gt;._ext.&lt;name&gt;"</c>) or a mirrored aligned scope
    /// (<c>"$._ext.&lt;name&gt;.&lt;parentRemainder&gt;._ext.&lt;name&gt;"</c> with
    /// matching extension names) and returns the implied parent collection scope.
    /// The structurally similar
    /// <c>"$._ext.&lt;X&gt;.&lt;parent&gt;._ext.&lt;Y&gt;"</c> shape with mismatched
    /// leading and trailing extension names does NOT qualify as mirrored; it falls
    /// through to the standard (non-mirrored) classification with the parent rooted
    /// under the extension-prefixed scope (i.e. the entire prefix preceding the
    /// trailing <c>._ext.&lt;Y&gt;</c> marker becomes the parent collection scope).
    /// Returns <c>null</c> only when the scope has no trailing
    /// <c>._ext.&lt;singleSegment&gt;</c> marker at all.
    /// </summary>
    public static AlignedExtensionScopeShape? Classify(string alignedScope)
    {
        ArgumentNullException.ThrowIfNull(alignedScope);

        var lastExt = alignedScope.LastIndexOf(TrailingMarker, StringComparison.Ordinal);
        if (lastExt < 0)
        {
            return null;
        }

        var afterLastExt = alignedScope.AsSpan(lastExt + TrailingMarker.Length);
        if (afterLastExt.IsEmpty || afterLastExt.IndexOf('.') >= 0 || afterLastExt.IndexOf('[') >= 0)
        {
            return null;
        }

        var trailingExtensionName = afterLastExt.ToString();
        var withoutTrailing = alignedScope[..lastExt];

        var mirroredPrefix = $"{MirroredRootPrefix}{trailingExtensionName}.";
        if (withoutTrailing.StartsWith(mirroredPrefix, StringComparison.Ordinal))
        {
            var parentScope = "$." + withoutTrailing[mirroredPrefix.Length..];
            return new AlignedExtensionScopeShape(parentScope, IsMirrored: true);
        }

        return new AlignedExtensionScopeShape(withoutTrailing, IsMirrored: false);
    }
}
