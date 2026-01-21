// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// The content type usage for a profile (read or write operations)
/// </summary>
public enum ProfileContentType
{
    Read,
    Write,
}

/// <summary>
/// Member selection mode for profile filtering
/// </summary>
public enum MemberSelection
{
    /// <summary>
    /// Only include members explicitly listed in the profile
    /// </summary>
    IncludeOnly,

    /// <summary>
    /// Exclude members explicitly listed in the profile
    /// </summary>
    ExcludeOnly,

    /// <summary>
    /// Include all members (can still filter child elements)
    /// </summary>
    IncludeAll,
}

/// <summary>
/// Filter mode for collection item filtering
/// </summary>
public enum FilterMode
{
    /// <summary>
    /// Only include items matching the filter values
    /// </summary>
    IncludeOnly,

    /// <summary>
    /// Exclude items matching the filter values
    /// </summary>
    ExcludeOnly,
}

/// <summary>
/// Profile context for the current request, containing the resolved profile information
/// </summary>
/// <param name="ProfileName">The name of the profile being applied</param>
/// <param name="ContentType">Whether this is for read or write operations</param>
/// <param name="ResourceProfile">The profile rules for the requested resource</param>
/// <param name="WasExplicitlySpecified">True if the profile was specified via header, false if implicitly selected</param>
public record ProfileContext(
    string ProfileName,
    ProfileContentType ContentType,
    ResourceProfile ResourceProfile,
    bool WasExplicitlySpecified
);

/// <summary>
/// A complete parsed profile definition containing rules for one or more resources
/// </summary>
/// <param name="ProfileName">The name of the profile</param>
/// <param name="Resources">The list of resources covered by this profile</param>
public record ProfileDefinition(string ProfileName, IReadOnlyList<ResourceProfile> Resources);

/// <summary>
/// Profile rules for a single resource
/// </summary>
/// <param name="ResourceName">The resource name (e.g., "Student", "School")</param>
/// <param name="LogicalSchema">Optional schema for extension resources</param>
/// <param name="ReadContentType">Rules for read operations, or null if not readable</param>
/// <param name="WriteContentType">Rules for write operations, or null if not writable</param>
public record ResourceProfile(
    string ResourceName,
    string? LogicalSchema,
    ContentTypeDefinition? ReadContentType,
    ContentTypeDefinition? WriteContentType
);

/// <summary>
/// Definition of content type rules for read or write operations
/// </summary>
/// <param name="MemberSelection">The member selection mode</param>
/// <param name="Properties">Property rules at the top level</param>
/// <param name="Objects">Nested object rules</param>
/// <param name="Collections">Collection rules</param>
/// <param name="Extensions">Extension rules</param>
public record ContentTypeDefinition(
    MemberSelection MemberSelection,
    IReadOnlyList<PropertyRule> Properties,
    IReadOnlyList<ObjectRule> Objects,
    IReadOnlyList<CollectionRule> Collections,
    IReadOnlyList<ExtensionRule> Extensions
)
{
    // Pre-computed lookups for efficient filtering (lazy-initialized)
    private HashSet<string>? _propertyNameSet;
    private Dictionary<string, ObjectRule>? _objectRulesByName;
    private Dictionary<string, CollectionRule>? _collectionRulesByName;
    private Dictionary<string, ExtensionRule>? _extensionRulesByName;

    /// <summary>Pre-computed set of property names for O(1) membership checking</summary>
    public HashSet<string> PropertyNameSet => _propertyNameSet ??= Properties.Select(p => p.Name).ToHashSet();

    /// <summary>Pre-computed dictionary of object rules by name</summary>
    public IReadOnlyDictionary<string, ObjectRule> ObjectRulesByName =>
        _objectRulesByName ??= Objects.ToDictionary(o => o.Name, o => o);

    /// <summary>Pre-computed dictionary of collection rules by name</summary>
    public IReadOnlyDictionary<string, CollectionRule> CollectionRulesByName =>
        _collectionRulesByName ??= Collections.ToDictionary(c => c.Name, c => c);

    /// <summary>Pre-computed dictionary of extension rules by name</summary>
    public IReadOnlyDictionary<string, ExtensionRule> ExtensionRulesByName =>
        _extensionRulesByName ??= Extensions.ToDictionary(e => e.Name, e => e);
}

/// <summary>
/// A rule for a property (scalar, descriptor, or reference)
/// </summary>
/// <param name="Name">The property name</param>
public record PropertyRule(string Name);

/// <summary>
/// A rule for a nested object within a resource
/// </summary>
/// <param name="Name">The object name</param>
/// <param name="MemberSelection">The member selection mode for this object</param>
/// <param name="LogicalSchema">Optional schema for extension objects</param>
/// <param name="Properties">Property rules within this object</param>
/// <param name="NestedObjects">Nested object rules (recursive)</param>
/// <param name="Collections">Collection rules within this object</param>
/// <param name="Extensions">Extension rules within this object</param>
public record ObjectRule(
    string Name,
    MemberSelection MemberSelection,
    string? LogicalSchema,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,
    IReadOnlyList<CollectionRule>? Collections,
    IReadOnlyList<ExtensionRule>? Extensions
)
{
    // Pre-computed lookups for efficient filtering (lazy-initialized)
    private HashSet<string>? _propertyNameSet;
    private Dictionary<string, ObjectRule>? _nestedObjectRulesByName;
    private Dictionary<string, CollectionRule>? _collectionRulesByName;
    private Dictionary<string, ExtensionRule>? _extensionRulesByName;

    /// <summary>Pre-computed set of property names for O(1) membership checking</summary>
    public HashSet<string> PropertyNameSet =>
        _propertyNameSet ??= Properties?.Select(p => p.Name).ToHashSet() ?? [];

    /// <summary>Pre-computed dictionary of nested object rules by name</summary>
    public IReadOnlyDictionary<string, ObjectRule> NestedObjectRulesByName =>
        _nestedObjectRulesByName ??=
            NestedObjects?.ToDictionary(o => o.Name, o => o) ?? new Dictionary<string, ObjectRule>();

    /// <summary>Pre-computed dictionary of collection rules by name</summary>
    public IReadOnlyDictionary<string, CollectionRule> CollectionRulesByName =>
        _collectionRulesByName ??=
            Collections?.ToDictionary(c => c.Name, c => c) ?? new Dictionary<string, CollectionRule>();

    /// <summary>Pre-computed dictionary of extension rules by name</summary>
    public IReadOnlyDictionary<string, ExtensionRule> ExtensionRulesByName =>
        _extensionRulesByName ??=
            Extensions?.ToDictionary(e => e.Name, e => e) ?? new Dictionary<string, ExtensionRule>();
}

/// <summary>
/// A rule for a collection within a resource
/// </summary>
/// <param name="Name">The collection name</param>
/// <param name="MemberSelection">The member selection mode for this collection</param>
/// <param name="LogicalSchema">Optional schema (inherited from ClassDefinition)</param>
/// <param name="Properties">Property rules for items in this collection</param>
/// <param name="NestedObjects">Nested object rules within collection items</param>
/// <param name="NestedCollections">Nested collection rules within collection items</param>
/// <param name="Extensions">Extension rules within collection items</param>
/// <param name="ItemFilter">Optional filter for collection items based on descriptor values</param>
public record CollectionRule(
    string Name,
    MemberSelection MemberSelection,
    string? LogicalSchema,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? NestedObjects,
    IReadOnlyList<CollectionRule>? NestedCollections,
    IReadOnlyList<ExtensionRule>? Extensions,
    CollectionItemFilter? ItemFilter
)
{
    // Pre-computed lookups for efficient filtering (lazy-initialized)
    private HashSet<string>? _propertyNameSet;
    private Dictionary<string, ObjectRule>? _nestedObjectRulesByName;
    private Dictionary<string, CollectionRule>? _nestedCollectionRulesByName;
    private Dictionary<string, ExtensionRule>? _extensionRulesByName;

    /// <summary>Pre-computed set of property names for O(1) membership checking</summary>
    public HashSet<string> PropertyNameSet =>
        _propertyNameSet ??= Properties?.Select(p => p.Name).ToHashSet() ?? [];

    /// <summary>Pre-computed dictionary of nested object rules by name</summary>
    public IReadOnlyDictionary<string, ObjectRule> NestedObjectRulesByName =>
        _nestedObjectRulesByName ??=
            NestedObjects?.ToDictionary(o => o.Name, o => o) ?? new Dictionary<string, ObjectRule>();

    /// <summary>Pre-computed dictionary of nested collection rules by name</summary>
    public IReadOnlyDictionary<string, CollectionRule> NestedCollectionRulesByName =>
        _nestedCollectionRulesByName ??=
            NestedCollections?.ToDictionary(c => c.Name, c => c) ?? new Dictionary<string, CollectionRule>();

    /// <summary>Pre-computed dictionary of extension rules by name</summary>
    public IReadOnlyDictionary<string, ExtensionRule> ExtensionRulesByName =>
        _extensionRulesByName ??=
            Extensions?.ToDictionary(e => e.Name, e => e) ?? new Dictionary<string, ExtensionRule>();
}

/// <summary>
/// A rule for an extension namespace within a resource
/// </summary>
/// <param name="Name">The extension namespace name (e.g., "Sample")</param>
/// <param name="MemberSelection">The member selection mode for this extension</param>
/// <param name="LogicalSchema">Optional schema for extension definitions</param>
/// <param name="Properties">Property rules within this extension</param>
/// <param name="Objects">Object rules within this extension</param>
/// <param name="Collections">Collection rules within this extension</param>
public record ExtensionRule(
    string Name,
    MemberSelection MemberSelection,
    string? LogicalSchema,
    IReadOnlyList<PropertyRule>? Properties,
    IReadOnlyList<ObjectRule>? Objects,
    IReadOnlyList<CollectionRule>? Collections
)
{
    // Pre-computed lookups for efficient filtering (lazy-initialized)
    private HashSet<string>? _propertyNameSet;
    private Dictionary<string, ObjectRule>? _objectRulesByName;
    private Dictionary<string, CollectionRule>? _collectionRulesByName;

    /// <summary>Pre-computed set of property names for O(1) membership checking</summary>
    public HashSet<string> PropertyNameSet =>
        _propertyNameSet ??= Properties?.Select(p => p.Name).ToHashSet() ?? [];

    /// <summary>Pre-computed dictionary of object rules by name</summary>
    public IReadOnlyDictionary<string, ObjectRule> ObjectRulesByName =>
        _objectRulesByName ??=
            Objects?.ToDictionary(o => o.Name, o => o) ?? new Dictionary<string, ObjectRule>();

    /// <summary>Pre-computed dictionary of collection rules by name</summary>
    public IReadOnlyDictionary<string, CollectionRule> CollectionRulesByName =>
        _collectionRulesByName ??=
            Collections?.ToDictionary(c => c.Name, c => c) ?? new Dictionary<string, CollectionRule>();
}

/// <summary>
/// A filter for collection items based on descriptor property values
/// </summary>
/// <param name="PropertyName">The descriptor property to filter on (e.g., "AddressTypeDescriptor")</param>
/// <param name="FilterMode">Whether to include or exclude matching items</param>
/// <param name="Values">The descriptor URI values to match</param>
public record CollectionItemFilter(string PropertyName, FilterMode FilterMode, IReadOnlyList<string> Values);
