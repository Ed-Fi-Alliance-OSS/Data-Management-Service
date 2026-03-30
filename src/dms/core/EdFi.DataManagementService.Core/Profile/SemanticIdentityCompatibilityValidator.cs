// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Pre-runtime validation gate that rejects writable profile definitions hiding
/// compiled semantic-identity fields for persisted multi-item collection scopes.
/// Implements Core responsibility #12 per profiles.md.
/// </summary>
/// <remarks>
/// <para>
/// This validator consumes the compiled-scope adapter contract from C1 and the
/// typed error contract from C8. It is a pure function: callers provide compiled
/// scope descriptors and a profile definition, and receive back a list of
/// structured failures (empty if the profile is valid).
/// </para>
/// <para>
/// Integration: This gate runs before request-time merge execution. C5 (pipeline
/// orchestration) or the production adapter factory (DMS-1106) calls this when
/// a writable profile is first associated with a resource's compiled scope catalog.
/// </para>
/// </remarks>
internal static class SemanticIdentityCompatibilityValidator
{
    /// <summary>
    /// Validates that a writable profile definition does not hide semantic identity
    /// members for any persisted multi-item collection scope in the compiled adapter.
    /// </summary>
    /// <param name="profileDefinition">The profile definition to validate.</param>
    /// <param name="resourceName">The resource name whose compiled scopes are being checked.</param>
    /// <param name="compiledScopes">The compiled scope descriptors for the resource.</param>
    /// <returns>
    /// An empty list if the profile is valid; one
    /// <see cref="HiddenSemanticIdentityMembersProfileDefinitionFailure"/> per
    /// incompatible collection scope otherwise.
    /// </returns>
    public static IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> Validate(
        ProfileDefinition profileDefinition,
        string resourceName,
        IReadOnlyList<CompiledScopeDescriptor> compiledScopes
    )
    {
        ArgumentNullException.ThrowIfNull(profileDefinition);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(compiledScopes);

        ContentTypeDefinition? writeContent = profileDefinition
            .Resources.FirstOrDefault(r =>
                r.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)
            )
            ?.WriteContentType;

        if (writeContent == null)
        {
            return [];
        }

        List<HiddenSemanticIdentityMembersProfileDefinitionFailure>? failures = null;

        foreach (CompiledScopeDescriptor scope in compiledScopes)
        {
            if (scope.ScopeKind != ScopeKind.Collection)
            {
                continue;
            }

            if (scope.SemanticIdentityRelativePathsInOrder.IsDefaultOrEmpty)
            {
                continue;
            }

            CollectionLookupResult lookup = FindCollectionInProfile(writeContent, scope.JsonScope);

            if (lookup.Visibility != CollectionVisibility.VisibleWithExplicitRules)
            {
                continue;
            }

            ImmutableArray<string> hiddenMembers = GetHiddenSemanticIdentityMembers(
                lookup.Rule!,
                scope.SemanticIdentityRelativePathsInOrder
            );

            if (!hiddenMembers.IsEmpty)
            {
                failures ??= [];
                failures.Add(
                    ProfileFailures.HiddenSemanticIdentityMembers(
                        profileName: profileDefinition.ProfileName,
                        resourceName: resourceName,
                        jsonScope: scope.JsonScope,
                        hiddenCanonicalMemberPaths: hiddenMembers
                    )
                );
            }
        }

        return (IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure>?)failures ?? [];
    }

    // -----------------------------------------------------------------------
    //  Profile tree navigation
    // -----------------------------------------------------------------------

    private enum CollectionVisibility
    {
        Hidden,
        VisibleAllMembers,
        VisibleWithExplicitRules,
    }

    private sealed record CollectionLookupResult(CollectionVisibility Visibility, CollectionRule? Rule);

    private static readonly CollectionLookupResult _hidden = new(CollectionVisibility.Hidden, null);

    private static readonly CollectionLookupResult _visibleAllMembers = new(
        CollectionVisibility.VisibleAllMembers,
        null
    );

    /// <summary>
    /// Navigates the profile definition tree to find the CollectionRule for a
    /// compiled <paramref name="jsonScope"/>. Returns the visibility status and
    /// rule (if the collection has explicit member filtering).
    /// </summary>
    private static CollectionLookupResult FindCollectionInProfile(
        ContentTypeDefinition writeContent,
        string jsonScope
    )
    {
        string[] segments = jsonScope.Split('.');
        int startIndex = segments[0] == "$" ? 1 : 0;

        return Navigate(ProfileTreeNode.From(writeContent), segments, startIndex);
    }

    private static CollectionLookupResult Navigate(ProfileTreeNode node, string[] segments, int index)
    {
        if (index >= segments.Length)
        {
            return _hidden;
        }

        // Last segment must be the target collection
        if (index == segments.Length - 1)
        {
            string collectionName = StripArraySuffix(segments[index]);
            return LookupCollection(node, collectionName);
        }

        string segment = segments[index];

        // Extension: _ext followed by extension name
        if (segment == "_ext")
        {
            if (index + 1 >= segments.Length)
            {
                return _hidden;
            }

            return NavigateExtension(node, segments[index + 1], segments, index + 2);
        }

        // Intermediate collection (e.g., "addresses[*]" in "$.addresses[*].periods[*]")
        if (segment.EndsWith("[*]", StringComparison.Ordinal))
        {
            return NavigateIntermediateCollection(node, StripArraySuffix(segment), segments, index + 1);
        }

        // Intermediate object
        return NavigateObject(node, segment, segments, index + 1);
    }

    private static CollectionLookupResult LookupCollection(ProfileTreeNode node, string name)
    {
        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.Collections.TryGetValue(name, out CollectionRule? rule)
                ? new(CollectionVisibility.VisibleWithExplicitRules, rule)
                : _hidden,

            MemberSelection.ExcludeOnly => node.Collections.TryGetValue(name, out CollectionRule? exRule)
                ? new(CollectionVisibility.VisibleWithExplicitRules, exRule)
                : _visibleAllMembers,

            MemberSelection.IncludeAll => node.Collections.TryGetValue(name, out CollectionRule? rule)
                ? new(CollectionVisibility.VisibleWithExplicitRules, rule)
                : _visibleAllMembers,

            _ => _hidden,
        };
    }

    private static CollectionLookupResult NavigateIntermediateCollection(
        ProfileTreeNode node,
        string name,
        string[] segments,
        int nextIndex
    )
    {
        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.Collections.TryGetValue(name, out CollectionRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : _hidden,

            MemberSelection.ExcludeOnly => node.Collections.TryGetValue(name, out CollectionRule? exRule)
                ? Navigate(ProfileTreeNode.From(exRule), segments, nextIndex)
                : _visibleAllMembers,

            MemberSelection.IncludeAll => node.Collections.TryGetValue(name, out CollectionRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : _visibleAllMembers,

            _ => _hidden,
        };
    }

    private static CollectionLookupResult NavigateObject(
        ProfileTreeNode node,
        string name,
        string[] segments,
        int nextIndex
    )
    {
        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.Objects.TryGetValue(name, out ObjectRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : _hidden,

            MemberSelection.ExcludeOnly => node.Objects.TryGetValue(name, out ObjectRule? exRule)
                ? Navigate(ProfileTreeNode.From(exRule), segments, nextIndex)
                : _visibleAllMembers,

            MemberSelection.IncludeAll => node.Objects.TryGetValue(name, out ObjectRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : _visibleAllMembers,

            _ => _hidden,
        };
    }

    private static CollectionLookupResult NavigateExtension(
        ProfileTreeNode node,
        string extensionName,
        string[] segments,
        int nextIndex
    )
    {
        if (node.Extensions == null)
        {
            return node.MemberSelection == MemberSelection.IncludeOnly ? _hidden : _visibleAllMembers;
        }

        return node.MemberSelection switch
        {
            MemberSelection.IncludeOnly => node.Extensions.TryGetValue(extensionName, out ExtensionRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : _hidden,

            MemberSelection.ExcludeOnly => node.Extensions.TryGetValue(
                extensionName,
                out ExtensionRule? exRule
            )
                ? Navigate(ProfileTreeNode.From(exRule), segments, nextIndex)
                : _visibleAllMembers,

            MemberSelection.IncludeAll => node.Extensions.TryGetValue(extensionName, out ExtensionRule? rule)
                ? Navigate(ProfileTreeNode.From(rule), segments, nextIndex)
                : _visibleAllMembers,

            _ => _hidden,
        };
    }

    // -----------------------------------------------------------------------
    //  Member visibility
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the subset of semantic identity paths that are hidden by the
    /// collection rule's member selection.
    /// </summary>
    private static ImmutableArray<string> GetHiddenSemanticIdentityMembers(
        CollectionRule rule,
        ImmutableArray<string> semanticIdentityPaths
    )
    {
        ImmutableArray<string>.Builder? hidden = null;

        foreach (string path in semanticIdentityPaths)
        {
            // For reference-backed paths like "schoolReference.schoolId", the
            // top-level member name ("schoolReference") is what appears in the
            // collection rule's PropertyNameSet. Including the reference member
            // includes all its descendant properties.
            string memberName = ExtractTopLevelMember(path);

            bool isHidden = rule.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !rule.PropertyNameSet.Contains(memberName),
                MemberSelection.ExcludeOnly => rule.PropertyNameSet.Contains(memberName),
                MemberSelection.IncludeAll => false,
                _ => false,
            };

            if (isHidden)
            {
                hidden ??= ImmutableArray.CreateBuilder<string>();
                hidden.Add(path);
            }
        }

        return hidden?.ToImmutable() ?? [];
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static string StripArraySuffix(string segment) =>
        segment.EndsWith("[*]", StringComparison.Ordinal) ? segment[..^3] : segment;

    /// <summary>
    /// Extracts the top-level member name from a scope-relative path.
    /// For dotted paths like "schoolReference.schoolId", returns "schoolReference".
    /// For flat paths like "classPeriodName", returns the path unchanged.
    /// </summary>
    private static string ExtractTopLevelMember(string path)
    {
        int dotIndex = path.IndexOf('.');
        return dotIndex >= 0 ? path[..dotIndex] : path;
    }

    /// <summary>
    /// Normalized view of a profile tree node for navigation. Adapts the
    /// differing property names across ContentTypeDefinition, CollectionRule,
    /// ObjectRule, and ExtensionRule into a uniform lookup surface.
    /// </summary>
    private readonly struct ProfileTreeNode(
        MemberSelection memberSelection,
        IReadOnlyDictionary<string, CollectionRule> collections,
        IReadOnlyDictionary<string, ObjectRule> objects,
        IReadOnlyDictionary<string, ExtensionRule>? extensions
    )
    {
        public MemberSelection MemberSelection => memberSelection;
        public IReadOnlyDictionary<string, CollectionRule> Collections => collections;
        public IReadOnlyDictionary<string, ObjectRule> Objects => objects;
        public IReadOnlyDictionary<string, ExtensionRule>? Extensions => extensions;

        public static ProfileTreeNode From(ContentTypeDefinition c) =>
            new(c.MemberSelection, c.CollectionRulesByName, c.ObjectRulesByName, c.ExtensionRulesByName);

        public static ProfileTreeNode From(CollectionRule c) =>
            new(
                c.MemberSelection,
                c.NestedCollectionRulesByName,
                c.NestedObjectRulesByName,
                c.ExtensionRulesByName
            );

        public static ProfileTreeNode From(ObjectRule o) =>
            new(
                o.MemberSelection,
                o.CollectionRulesByName,
                o.NestedObjectRulesByName,
                o.ExtensionRulesByName
            );

        public static ProfileTreeNode From(ExtensionRule e) =>
            new(e.MemberSelection, e.CollectionRulesByName, e.ObjectRulesByName, null);
    }
}
