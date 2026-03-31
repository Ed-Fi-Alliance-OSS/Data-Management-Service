// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Normalized view of a profile tree node for navigation. Adapts the
/// differing property names across ContentTypeDefinition, CollectionRule,
/// ObjectRule, and ExtensionRule into a uniform lookup surface.
/// </summary>
/// <param name="MemberSelection">The member selection mode at this node.</param>
/// <param name="ExplicitPropertyNames">
/// For IncludeOnly: the included property names.
/// For ExcludeOnly: the excluded property names.
/// For IncludeAll: empty.
/// </param>
/// <param name="CollectionsByName">Named collection rules at this level.</param>
/// <param name="ObjectsByName">Named object rules at this level.</param>
/// <param name="ExtensionsByName">Named extension rules at this level, or null if extensions are not applicable.</param>
public readonly record struct ProfileTreeNode(
    MemberSelection MemberSelection,
    IReadOnlySet<string> ExplicitPropertyNames,
    IReadOnlyDictionary<string, CollectionRule> CollectionsByName,
    IReadOnlyDictionary<string, ObjectRule> ObjectsByName,
    IReadOnlyDictionary<string, ExtensionRule>? ExtensionsByName
)
{
    private static readonly IReadOnlySet<string> _emptyPropertyNames = new HashSet<string>();
    private static readonly IReadOnlyDictionary<string, CollectionRule> _emptyCollections =
        new Dictionary<string, CollectionRule>();
    private static readonly IReadOnlyDictionary<string, ObjectRule> _emptyObjects =
        new Dictionary<string, ObjectRule>();

    internal static ProfileTreeNode From(ContentTypeDefinition c) =>
        new(
            c.MemberSelection,
            c.PropertyNameSet,
            c.CollectionRulesByName,
            c.ObjectRulesByName,
            c.ExtensionRulesByName
        );

    internal static ProfileTreeNode From(CollectionRule c) =>
        new(
            c.MemberSelection,
            c.PropertyNameSet,
            c.NestedCollectionRulesByName,
            c.NestedObjectRulesByName,
            c.ExtensionRulesByName
        );

    internal static ProfileTreeNode From(ObjectRule o) =>
        new(
            o.MemberSelection,
            o.PropertyNameSet,
            o.CollectionRulesByName,
            o.NestedObjectRulesByName,
            o.ExtensionRulesByName
        );

    internal static ProfileTreeNode From(ExtensionRule e) =>
        new(e.MemberSelection, e.PropertyNameSet, e.CollectionRulesByName, e.ObjectRulesByName, null);

    internal static ProfileTreeNode IncludeAllDefault() =>
        new(MemberSelection.IncludeAll, _emptyPropertyNames, _emptyCollections, _emptyObjects, null);
}

/// <summary>
/// Navigates a profile definition tree using compiled JsonScope paths, returning
/// a <see cref="ProfileTreeNode"/> for a target scope. Returns null when the
/// scope is hidden by the profile's member selection rules.
/// </summary>
/// <remarks>
/// <para>
/// Navigation rules per <see cref="MemberSelection"/>:
/// <list type="bullet">
/// <item><term>IncludeOnly</term><description>The named member must appear in the parent's rule
/// dictionary; otherwise null (hidden) is returned.</description></item>
/// <item><term>ExcludeOnly</term><description>If the named member appears in the parent's rule
/// dictionary the rule is used; if absent, <see cref="ProfileTreeNode.IncludeAllDefault"/> is
/// returned (the member is visible with no explicit filtering).</description></item>
/// <item><term>IncludeAll</term><description>Same as ExcludeOnly: explicit rule if present,
/// otherwise <see cref="ProfileTreeNode.IncludeAllDefault"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// Calling Navigate with "$" returns the root node directly from the ContentTypeDefinition
/// without consuming any segment-level rules.
/// </para>
/// </remarks>
public sealed class ProfileTreeNavigator(ContentTypeDefinition writeContentType)
{
    /// <summary>
    /// Navigates the profile tree to the node corresponding to the given
    /// compiled <paramref name="jsonScope"/> path.
    /// </summary>
    /// <param name="jsonScope">
    /// A compiled scope path such as "$", "$.classPeriods[*]", or
    /// "$._ext.sample.things[*]".
    /// </param>
    /// <returns>
    /// The <see cref="ProfileTreeNode"/> for the scope, or null if the scope
    /// is hidden by the profile's member selection.
    /// </returns>
    public ProfileTreeNode? Navigate(string jsonScope)
    {
        if (jsonScope == "$")
        {
            return ProfileTreeNode.From(writeContentType);
        }

        string[] segments = jsonScope.Split('.');
        // segments[0] is "$" — skip it
        int startIndex = segments[0] == "$" ? 1 : 0;

        return Navigate(ProfileTreeNode.From(writeContentType), segments, startIndex);
    }

    private static ProfileTreeNode? Navigate(ProfileTreeNode node, string[] segments, int index)
    {
        if (index >= segments.Length)
        {
            return node;
        }

        string segment = segments[index];

        // Extension segment: _ext followed by extension name
        if (segment == "_ext")
        {
            if (index + 1 >= segments.Length)
            {
                return null;
            }

            return NavigateExtension(node, segments[index + 1], segments, index + 2);
        }

        // Collection segment (e.g. "classPeriods[*]")
        if (segment.EndsWith("[*]", StringComparison.Ordinal))
        {
            return NavigateCollection(node, StripArraySuffix(segment), segments, index + 1);
        }

        // Object segment (non-collection, non-extension)
        return NavigateObject(node, segment, segments, index + 1);
    }

    private static ProfileTreeNode? NavigateCollection(
        ProfileTreeNode node,
        string name,
        string[] segments,
        int nextIndex
    )
    {
        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.CollectionsByName.TryGetValue(name, out CollectionRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : null,

            MemberSelection.ExcludeOnly => node.CollectionsByName.TryGetValue(
                name,
                out CollectionRule? exRule
            )
                ? Navigate(ProfileTreeNode.From(exRule), segments, nextIndex)
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex),

            MemberSelection.IncludeAll => node.CollectionsByName.TryGetValue(name, out CollectionRule? iaRule)
                ? Navigate(ProfileTreeNode.From(iaRule), segments, nextIndex)
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex),

            _ => null,
        };
    }

    private static ProfileTreeNode? NavigateObject(
        ProfileTreeNode node,
        string name,
        string[] segments,
        int nextIndex
    )
    {
        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.ObjectsByName.TryGetValue(name, out ObjectRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : null,

            MemberSelection.ExcludeOnly => node.ObjectsByName.TryGetValue(name, out ObjectRule? exRule)
                ? Navigate(ProfileTreeNode.From(exRule), segments, nextIndex)
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex),

            MemberSelection.IncludeAll => node.ObjectsByName.TryGetValue(name, out ObjectRule? iaRule)
                ? Navigate(ProfileTreeNode.From(iaRule), segments, nextIndex)
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex),

            _ => null,
        };
    }

    private static ProfileTreeNode? NavigateExtension(
        ProfileTreeNode node,
        string extensionName,
        string[] segments,
        int nextIndex
    )
    {
        if (node.ExtensionsByName == null)
        {
            return node.MemberSelection == MemberSelection.IncludeOnly
                ? null
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex);
        }

        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.ExtensionsByName.TryGetValue(
                extensionName,
                out ExtensionRule? rule
            )
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : null,

            MemberSelection.ExcludeOnly => node.ExtensionsByName.TryGetValue(
                extensionName,
                out ExtensionRule? exRule
            )
                ? Navigate(ProfileTreeNode.From(exRule), segments, nextIndex)
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex),

            MemberSelection.IncludeAll => node.ExtensionsByName.TryGetValue(
                extensionName,
                out ExtensionRule? iaRule
            )
                ? Navigate(ProfileTreeNode.From(iaRule), segments, nextIndex)
                : Navigate(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex),

            _ => null,
        };
    }

    private static string StripArraySuffix(string segment) =>
        segment.EndsWith("[*]", StringComparison.Ordinal) ? segment[..^3] : segment;
}
