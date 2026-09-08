// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

/// <summary>
/// Shared test fixtures for profile-related tests. Provides scope catalogs and
/// profile definitions used across ProfileTreeNavigator, ProfileVisibilityClassifier,
/// and WritableRequestShaper test suites.
/// </summary>
internal static class ProfileTestFixtures
{
    // -----------------------------------------------------------------------
    //  Scope catalogs
    // -----------------------------------------------------------------------

    /// <summary>
    /// Compiled scope descriptors for a StudentSchoolAssociation-like fixture:
    /// root ($), calendarReference (NonCollection), classPeriods (Collection).
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths:
                [
                    "studentReference.studentUniqueId",
                    "schoolReference.schoolId",
                    "entryDate",
                    "entryTypeDescriptor",
                ]
            ),
            new(
                JsonScope: "$.calendarReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["calendarCode", "calendarTypeDescriptor"]
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName", "officialAttendancePeriod"]
            ),
        ];

    /// <summary>
    /// Scope catalog with an addresses collection for value-filter tests.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> AddressesFixtureScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["field1"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                CanonicalScopeRelativeMemberPaths:
                [
                    "addressTypeDescriptor",
                    "city",
                    "stateAbbreviationDescriptor",
                ]
            ),
        ];

    /// <summary>
    /// Scope catalog with root-level extension scope and a collection with
    /// an extension scope within items.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> ExtensionFixtureScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["studentReference.studentUniqueId", "entryDate"]
            ),
            new(
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["sampleField"]
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*]._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["extraField"]
            ),
        ];

    /// <summary>
    /// Scope catalog with a non-collection scope nested inside a collection.
    /// Used to test EmitMissingScopeStates does not throw for scopes with
    /// collection ancestors.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> NestedNonCollectionInsideCollectionScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["field1"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                CanonicalScopeRelativeMemberPaths: ["addressTypeDescriptor", "city"]
            ),
            new(
                JsonScope: "$.addresses[*].period",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.addresses[*]",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["beginDate", "endDate"]
            ),
        ];

    /// <summary>
    /// Scope catalog with a two-level non-collection hierarchy inside a collection.
    /// Used to test that descendants of hidden/absent non-collection parents are
    /// emitted by the stored-side walker.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> DeepNestedNonCollectionInsideCollectionScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["field1"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                CanonicalScopeRelativeMemberPaths: ["addressTypeDescriptor", "city"]
            ),
            new(
                JsonScope: "$.addresses[*].period",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.addresses[*]",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["beginDate", "endDate"]
            ),
            new(
                JsonScope: "$.addresses[*].period.detail",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.addresses[*].period",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["description", "category"]
            ),
        ];

    // -----------------------------------------------------------------------
    //  Profile definitions
    // -----------------------------------------------------------------------

    /// <summary>
    /// IncludeAll profile with no explicit rules. Every scope and member is visible.
    /// </summary>
    public static ContentTypeDefinition BuildIncludeAllProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects: [],
            Collections: [],
            Extensions: []
        );

    /// <summary>
    /// IncludeOnly profile with root-level extension and a collection-level extension.
    /// Used for extension scope shaping tests.
    /// </summary>
    public static ContentTypeDefinition BuildExtensionProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("studentReference"), new PropertyRule("entryDate")],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("classPeriodName")],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions:
                    [
                        new ExtensionRule(
                            Name: "sample",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("extraField")],
                            Objects: null,
                            Collections: null
                        ),
                    ],
                    ItemFilter: null
                ),
            ],
            Extensions:
            [
                new ExtensionRule(
                    Name: "sample",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("sampleField")],
                    Objects: null,
                    Collections: null
                ),
            ]
        );

    /// <summary>
    /// IncludeOnly profile for deep-nested addresses that includes addresses and
    /// period (with beginDate) but hides period.detail by omitting it from the
    /// NestedObjects list. Used to verify descendant emission when a deeper scope
    /// is hidden.
    /// </summary>
    public static ContentTypeDefinition BuildDeepNestedAddressesIncludeOnlyProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("field1")],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "addresses",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("addressTypeDescriptor"), new PropertyRule("city")],
                    NestedObjects:
                    [
                        new ObjectRule(
                            Name: "period",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("beginDate")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// IncludeOnly profile for deep-nested addresses that includes addresses but
    /// hides the period scope entirely (not listed in NestedObjects). Used to verify
    /// that descendants of a hidden non-collection parent are still emitted.
    /// </summary>
    public static ContentTypeDefinition BuildDeepNestedAddressesHiddenPeriodProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("field1")],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "addresses",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("addressTypeDescriptor"), new PropertyRule("city")],
                    NestedObjects: null,
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// Scope catalog with root-level extension scope containing a collection.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> ExtensionWithCollectionFixtureScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["studentReference.studentUniqueId", "entryDate"]
            ),
            new(
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["sampleField"]
            ),
            new(
                JsonScope: "$._ext.sample.extActivities[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$._ext.sample",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["activityName"],
                CanonicalScopeRelativeMemberPaths: ["activityName", "activityDescription"]
            ),
        ];

    /// <summary>
    /// Scope catalog with a non-collection scope nested inside another non-collection scope.
    /// Used to test recursive shaping of embedded/common-type scopes per C3 acceptance criteria.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> NestedNonCollectionInNonCollectionScopes =>
        [
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["field1"]
            ),
            new(
                JsonScope: "$.parentObject",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["parentField", "sharedField"]
            ),
            new(
                JsonScope: "$.parentObject.nestedCommonType",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.parentObject",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["nestedField", "hiddenNestedField"]
            ),
        ];

    /// <summary>
    /// IncludeOnly profile exposing parentObject with an IncludeOnly nested common type.
    /// Used for recursive non-collection scope shaping tests.
    /// </summary>
    public static ContentTypeDefinition BuildNestedNonCollectionProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("field1")],
            Objects:
            [
                new ObjectRule(
                    Name: "parentObject",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("parentField")],
                    NestedObjects:
                    [
                        new ObjectRule(
                            Name: "nestedCommonType",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("nestedField")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    Collections: null,
                    Extensions: null
                ),
            ],
            Collections: [],
            Extensions: []
        );

    /// <summary>
    /// IncludeOnly profile with root-level extension containing a collection.
    /// Used for extension-collection shaping tests.
    /// </summary>
    public static ContentTypeDefinition BuildExtensionWithCollectionProfile() =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties: [new PropertyRule("studentReference"), new PropertyRule("entryDate")],
            Objects: [],
            Collections: [],
            Extensions:
            [
                new ExtensionRule(
                    Name: "sample",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("sampleField")],
                    Objects: null,
                    Collections:
                    [
                        new CollectionRule(
                            Name: "extActivities",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("activityName")],
                            NestedObjects: null,
                            NestedCollections: null,
                            Extensions: null,
                            ItemFilter: null
                        ),
                    ]
                ),
            ]
        );
}
