# DMS-1115: Request-Side Visibility Classification + Writable Request Shaping

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement request-side visibility classification and writable request shaping, producing `WritableRequestBody`, `RequestScopeState` entries, and `VisibleRequestCollectionItem` entries for the Core profile write pipeline.

**Architecture:** Two-layer design. A shared `ProfileVisibilityClassifier` (reusable primitive for C5/C6) sits beneath a `WritableRequestShaper` that walks the request body once, building shaped JSON while emitting scope states, collection items, and validation failures. A `ProfileTreeNavigator` extracted from C2's validator provides the shared profile-to-scope bridging.

**Tech Stack:** C# / .NET, System.Text.Json, System.Collections.Immutable, NUnit, FluentAssertions

**Spec:** `docs/superpowers/specs/2026-03-30-dms-1115-request-visibility-and-writable-shaping-design.md`

---

## File Map

| File | Project | Action | Responsibility |
| --- | --- | --- | --- |
| `src/dms/core/EdFi.DataManagementService.Core.External/Profile/ProfileVisibilityKind.cs` | Core.External | Create | `ProfileVisibilityKind` enum |
| `src/dms/core/EdFi.DataManagementService.Core.External/Profile/RequestScopeState.cs` | Core.External | Create | `RequestScopeState` record |
| `src/dms/core/EdFi.DataManagementService.Core.External/Profile/VisibleRequestCollectionItem.cs` | Core.External | Create | `VisibleRequestCollectionItem` record |
| `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileTreeNavigator.cs` | Core | Create | Shared profile tree navigation |
| `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileVisibilityClassifier.cs` | Core | Create | Shared visibility primitive |
| `src/dms/core/EdFi.DataManagementService.Core/Profile/WritableRequestShaper.cs` | Core | Create | Request-side shaping + enumeration |
| `src/dms/core/EdFi.DataManagementService.Core/Profile/SemanticIdentityCompatibilityValidator.cs` | Core | Modify | Replace private navigation with `ProfileTreeNavigator` |
| `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileTreeNavigatorTests.cs` | Tests.Unit | Create | Navigator tests |
| `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileVisibilityClassifierTests.cs` | Tests.Unit | Create | Classifier tests |
| `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/WritableRequestShaperTests.cs` | Tests.Unit | Create | Shaper tests |

---

## Task 1: Contract Types

Define the three new external contract types consumed by downstream stories.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core.External/Profile/ProfileVisibilityKind.cs`
- Create: `src/dms/core/EdFi.DataManagementService.Core.External/Profile/RequestScopeState.cs`
- Create: `src/dms/core/EdFi.DataManagementService.Core.External/Profile/VisibleRequestCollectionItem.cs`

- [ ] **Step 1: Create `ProfileVisibilityKind.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Classifies the visibility of a compiled scope relative to a writable profile
/// and the data present in a JSON document (request or stored).
/// </summary>
public enum ProfileVisibilityKind
{
    /// <summary>
    /// Scope is included in the writable profile and the document provides data for it.
    /// </summary>
    VisiblePresent,

    /// <summary>
    /// Scope is included in the writable profile but the document does not provide data
    /// for it. Backend must distinguish this from Hidden to clear stored data correctly.
    /// </summary>
    VisibleAbsent,

    /// <summary>
    /// Scope is excluded from the writable profile. Backend must preserve stored data.
    /// </summary>
    Hidden,
}
```

- [ ] **Step 2: Create `RequestScopeState.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Request-side state for a non-collection compiled scope. Emitted by C3
/// (request-side visibility classification) with <see cref="Creatable"/> set
/// to false; C4 (creatability analysis) enriches the flag.
/// </summary>
/// <param name="Address">Stable scope instance address derived by C1.</param>
/// <param name="Visibility">Visibility classification relative to the writable profile and request data.</param>
/// <param name="Creatable">
/// Whether a new scope instance may be created. Initially false; populated by C4.
/// </param>
public sealed record RequestScopeState(
    ScopeInstanceAddress Address,
    ProfileVisibilityKind Visibility,
    bool Creatable
);
```

- [ ] **Step 3: Create `VisibleRequestCollectionItem.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Request-side state for a visible collection item. Emitted by C3 with
/// <see cref="Creatable"/> set to false; C4 enriches the flag.
/// </summary>
/// <param name="Address">Stable collection row address derived by C1.</param>
/// <param name="Creatable">
/// Whether a new collection item may be created. Initially false; populated by C4.
/// </param>
public sealed record VisibleRequestCollectionItem(
    CollectionRowAddress Address,
    bool Creatable
);
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core.External/EdFi.DataManagementService.Core.External.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/dms/core/EdFi.DataManagementService.Core.External/Profile/ProfileVisibilityKind.cs src/dms/core/EdFi.DataManagementService.Core.External/Profile/RequestScopeState.cs src/dms/core/EdFi.DataManagementService.Core.External/Profile/VisibleRequestCollectionItem.cs
git commit -m "[DMS-1115] Add ProfileVisibilityKind, RequestScopeState, and VisibleRequestCollectionItem contract types"
```

---

## Task 2: ProfileTreeNavigator — Tests

Write failing tests for the shared profile tree navigator before implementing it.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileTreeNavigatorTests.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileContext.cs` — `ContentTypeDefinition`, `MemberSelection`, rule types
- `src/dms/core/EdFi.DataManagementService.Core/Profile/SemanticIdentityCompatibilityValidator.cs` — existing navigation pattern to extract
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/SemanticIdentityCompatibilityValidatorTests.cs` — test style and shared fixture

- [ ] **Step 1: Write `ProfileTreeNavigatorTests.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileTreeNavigatorTests
{
    /// <summary>
    /// Shared writable profile: RestrictedAssociation-Write from the delivery plan.
    /// IncludeOnly at root exposing studentReference, schoolReference, entryDate, classPeriods.
    /// Hides entryTypeDescriptor and calendarReference.
    /// classPeriods is IncludeOnly with classPeriodName only (hides officialAttendancePeriod).
    /// </summary>
    protected static ContentTypeDefinition SharedWriteContent =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
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
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// Profile with IncludeAll at root, an object rule for calendarReference, and an extension.
    /// </summary>
    protected static ContentTypeDefinition IncludeAllWithObjectAndExtension =>
        new(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects:
            [
                new ObjectRule(
                    Name: "calendarReference",
                    MemberSelection: MemberSelection.IncludeOnly,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("calendarCode")],
                    NestedObjects: null,
                    Collections: null,
                    Extensions: null
                ),
            ],
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
                            MemberSelection: MemberSelection.IncludeAll,
                            LogicalSchema: null,
                            Properties: null,
                            NestedObjects: null,
                            NestedCollections: null,
                            Extensions: null,
                            ItemFilter: null
                        ),
                    ]
                ),
            ]
        );

    /// <summary>
    /// Profile with nested collections: classPeriods[*].meetingTimes[*].
    /// </summary>
    protected static ContentTypeDefinition NestedCollectionProfile =>
        new(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects: [],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("classPeriodName")],
                    NestedObjects: null,
                    NestedCollections:
                    [
                        new CollectionRule(
                            Name: "meetingTimes",
                            MemberSelection: MemberSelection.IncludeOnly,
                            LogicalSchema: null,
                            Properties:
                            [
                                new PropertyRule("startTime"),
                                new PropertyRule("endTime"),
                            ],
                            NestedObjects: null,
                            NestedCollections: null,
                            Extensions: null,
                            ItemFilter: null
                        ),
                    ],
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    [TestFixture]
    public class Given_Root_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            var navigator = new ProfileTreeNavigator(SharedWriteContent);
            _result = navigator.Navigate("$");
        }

        [Test]
        public void It_returns_a_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_the_root_member_selection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_has_the_root_property_names()
        {
            _result!.Value.ExplicitPropertyNames
                .Should()
                .BeEquivalentTo(["studentReference", "schoolReference", "entryDate"]);
        }

        [Test]
        public void It_has_the_root_collections()
        {
            _result!.Value.CollectionsByName.Should().ContainKey("classPeriods");
        }
    }

    [TestFixture]
    public class Given_Non_Collection_Child_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            var navigator = new ProfileTreeNavigator(IncludeAllWithObjectAndExtension);
            _result = navigator.Navigate("$.calendarReference");
        }

        [Test]
        public void It_returns_a_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_the_object_member_selection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_has_the_object_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().BeEquivalentTo(["calendarCode"]);
        }
    }

    [TestFixture]
    public class Given_Collection_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            var navigator = new ProfileTreeNavigator(SharedWriteContent);
            _result = navigator.Navigate("$.classPeriods[*]");
        }

        [Test]
        public void It_returns_a_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_the_collection_member_selection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_has_the_collection_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().BeEquivalentTo(["classPeriodName"]);
        }
    }

    [TestFixture]
    public class Given_Nested_Collection_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            var navigator = new ProfileTreeNavigator(NestedCollectionProfile);
            _result = navigator.Navigate("$.classPeriods[*].meetingTimes[*]");
        }

        [Test]
        public void It_returns_a_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_the_nested_collection_member_selection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_has_the_nested_collection_property_names()
        {
            _result!.Value.ExplicitPropertyNames
                .Should()
                .BeEquivalentTo(["startTime", "endTime"]);
        }
    }

    [TestFixture]
    public class Given_Extension_Scope : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            var navigator = new ProfileTreeNavigator(IncludeAllWithObjectAndExtension);
            _result = navigator.Navigate("$._ext.sample");
        }

        [Test]
        public void It_returns_a_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_the_extension_member_selection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_has_the_extension_property_names()
        {
            _result!.Value.ExplicitPropertyNames.Should().BeEquivalentTo(["sampleField"]);
        }

        [Test]
        public void It_has_the_extension_collections()
        {
            _result!.Value.CollectionsByName.Should().ContainKey("extActivities");
        }
    }

    [TestFixture]
    public class Given_Extension_Collection_Within_Root_Extension : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            var navigator = new ProfileTreeNavigator(IncludeAllWithObjectAndExtension);
            _result = navigator.Navigate("$._ext.sample.extActivities[*]");
        }

        [Test]
        public void It_returns_a_node()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_the_extension_collection_member_selection()
        {
            _result!.Value.MemberSelection.Should().Be(MemberSelection.IncludeAll);
        }
    }

    [TestFixture]
    public class Given_Scope_Not_In_IncludeOnly_Profile : ProfileTreeNavigatorTests
    {
        private ProfileTreeNode? _result;

        [SetUp]
        public void Setup()
        {
            // SharedWriteContent is IncludeOnly and does not include calendarReference
            var navigator = new ProfileTreeNavigator(SharedWriteContent);
            _result = navigator.Navigate("$.calendarReference");
        }

        [Test]
        public void It_returns_null()
        {
            _result.Should().BeNull();
        }
    }
}
```

- [ ] **Step 2: Verify tests fail (ProfileTreeNavigator does not exist yet)**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj`
Expected: Build FAILS — `ProfileTreeNavigator` and `ProfileTreeNode` not found

- [ ] **Step 3: Commit failing tests**

```bash
git add src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileTreeNavigatorTests.cs
git commit -m "[DMS-1115] Add failing tests for ProfileTreeNavigator"
```

---

## Task 3: ProfileTreeNavigator — Implementation

Extract and generalize the profile tree navigation from `SemanticIdentityCompatibilityValidator` into a shared class.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileTreeNavigator.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core/Profile/SemanticIdentityCompatibilityValidator.cs` — lines 106-379, the private `ProfileTreeNode` struct and navigation methods to extract

- [ ] **Step 1: Create `ProfileTreeNavigator.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Normalized view of a profile tree node for navigation. Adapts the differing
/// property names across <see cref="ContentTypeDefinition"/>,
/// <see cref="CollectionRule"/>, <see cref="ObjectRule"/>, and
/// <see cref="ExtensionRule"/> into a uniform lookup surface.
/// </summary>
/// <param name="MemberSelection">The member selection mode at this tree position.</param>
/// <param name="ExplicitPropertyNames">
/// For IncludeOnly: the set of explicitly included property names.
/// For ExcludeOnly: the set of explicitly excluded property names.
/// For IncludeAll: empty set.
/// </param>
/// <param name="CollectionsByName">Collection rules keyed by name.</param>
/// <param name="ObjectsByName">Object rules keyed by name.</param>
/// <param name="ExtensionsByName">Extension rules keyed by name, or null if extensions are not applicable.</param>
public readonly record struct ProfileTreeNode(
    MemberSelection MemberSelection,
    IReadOnlySet<string> ExplicitPropertyNames,
    IReadOnlyDictionary<string, CollectionRule> CollectionsByName,
    IReadOnlyDictionary<string, ObjectRule> ObjectsByName,
    IReadOnlyDictionary<string, ExtensionRule>? ExtensionsByName
)
{
    private static readonly IReadOnlySet<string> _emptySet = new HashSet<string>();

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
        new(
            e.MemberSelection,
            e.PropertyNameSet,
            e.CollectionRulesByName,
            e.ObjectRulesByName,
            null
        );

    /// <summary>
    /// Creates an IncludeAll node with no explicit rules — represents a scope that
    /// is visible because its ancestor uses IncludeAll or ExcludeOnly and the scope
    /// has no explicit rule entry.
    /// </summary>
    internal static ProfileTreeNode IncludeAllDefault() =>
        new(MemberSelection.IncludeAll, _emptySet, _emptyCollections, _emptyObjects, null);
}

/// <summary>
/// Navigates a <see cref="ContentTypeDefinition"/> profile tree to find the
/// <see cref="ProfileTreeNode"/> governing a compiled JsonScope path.
/// Shared between C2 (semantic identity validation) and C3 (visibility classification).
/// </summary>
public sealed class ProfileTreeNavigator
{
    private readonly ContentTypeDefinition _writeContentType;

    public ProfileTreeNavigator(ContentTypeDefinition writeContentType)
    {
        ArgumentNullException.ThrowIfNull(writeContentType);
        _writeContentType = writeContentType;
    }

    /// <summary>
    /// Navigate to the profile rules for a given compiled scope.
    /// Returns null if the scope is hidden because an ancestor uses IncludeOnly
    /// and does not list this scope's segment.
    /// </summary>
    /// <param name="jsonScope">Compiled JsonScope (e.g., "$", "$.classPeriods[*]", "$._ext.sample").</param>
    public ProfileTreeNode? Navigate(string jsonScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonScope);

        if (jsonScope == "$")
        {
            return ProfileTreeNode.From(_writeContentType);
        }

        string[] segments = jsonScope.Split('.');
        int startIndex = segments[0] == "$" ? 1 : 0;

        return NavigateSegments(ProfileTreeNode.From(_writeContentType), segments, startIndex);
    }

    private static ProfileTreeNode? NavigateSegments(ProfileTreeNode node, string[] segments, int index)
    {
        if (index >= segments.Length)
        {
            return null;
        }

        string segment = segments[index];
        bool isLastSegment = index == segments.Length - 1;

        // Extension: _ext followed by extension name
        if (segment == "_ext")
        {
            if (index + 1 >= segments.Length)
            {
                return null;
            }

            return NavigateExtension(node, segments[index + 1], segments, index + 2);
        }

        // Collection segment (has [*] suffix)
        if (segment.EndsWith("[*]", StringComparison.Ordinal))
        {
            string name = StripArraySuffix(segment);
            return NavigateCollection(node, name, segments, index, isLastSegment);
        }

        // Object segment
        return NavigateObject(node, segment, segments, index, isLastSegment);
    }

    private static ProfileTreeNode? NavigateCollection(
        ProfileTreeNode node,
        string name,
        string[] segments,
        int index,
        bool isLastSegment
    )
    {
        switch (node.MemberSelection)
        {
            case MemberSelection.IncludeOnly:
                if (!node.CollectionsByName.TryGetValue(name, out CollectionRule? rule))
                {
                    return null; // hidden — not in IncludeOnly list
                }
                return isLastSegment
                    ? ProfileTreeNode.From(rule)
                    : NavigateSegments(ProfileTreeNode.From(rule), segments, index + 1);

            case MemberSelection.ExcludeOnly:
                if (node.CollectionsByName.TryGetValue(name, out CollectionRule? exRule))
                {
                    return isLastSegment
                        ? ProfileTreeNode.From(exRule)
                        : NavigateSegments(ProfileTreeNode.From(exRule), segments, index + 1);
                }
                // Not excluded, visible with all members
                return isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, index + 1);

            case MemberSelection.IncludeAll:
                if (node.CollectionsByName.TryGetValue(name, out CollectionRule? allRule))
                {
                    return isLastSegment
                        ? ProfileTreeNode.From(allRule)
                        : NavigateSegments(ProfileTreeNode.From(allRule), segments, index + 1);
                }
                return isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, index + 1);

            default:
                return null;
        }
    }

    private static ProfileTreeNode? NavigateObject(
        ProfileTreeNode node,
        string name,
        string[] segments,
        int index,
        bool isLastSegment
    )
    {
        switch (node.MemberSelection)
        {
            case MemberSelection.IncludeOnly:
                if (!node.ObjectsByName.TryGetValue(name, out ObjectRule? rule))
                {
                    return null; // hidden
                }
                return isLastSegment
                    ? ProfileTreeNode.From(rule)
                    : NavigateSegments(ProfileTreeNode.From(rule), segments, index + 1);

            case MemberSelection.ExcludeOnly:
                if (node.ObjectsByName.TryGetValue(name, out ObjectRule? exRule))
                {
                    return isLastSegment
                        ? ProfileTreeNode.From(exRule)
                        : NavigateSegments(ProfileTreeNode.From(exRule), segments, index + 1);
                }
                return isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, index + 1);

            case MemberSelection.IncludeAll:
                if (node.ObjectsByName.TryGetValue(name, out ObjectRule? allRule))
                {
                    return isLastSegment
                        ? ProfileTreeNode.From(allRule)
                        : NavigateSegments(ProfileTreeNode.From(allRule), segments, index + 1);
                }
                return isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, index + 1);

            default:
                return null;
        }
    }

    private static ProfileTreeNode? NavigateExtension(
        ProfileTreeNode node,
        string extensionName,
        string[] segments,
        int nextIndex
    )
    {
        bool isLastSegment = nextIndex >= segments.Length;

        if (node.ExtensionsByName == null)
        {
            // No extension rules at this level
            return node.MemberSelection == MemberSelection.IncludeOnly
                ? null
                : isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex);
        }

        switch (node.MemberSelection)
        {
            case MemberSelection.IncludeOnly:
                if (!node.ExtensionsByName.TryGetValue(extensionName, out ExtensionRule? rule))
                {
                    return null;
                }
                return isLastSegment
                    ? ProfileTreeNode.From(rule)
                    : NavigateSegments(ProfileTreeNode.From(rule), segments, nextIndex);

            case MemberSelection.ExcludeOnly:
                if (node.ExtensionsByName.TryGetValue(extensionName, out ExtensionRule? exRule))
                {
                    return isLastSegment
                        ? ProfileTreeNode.From(exRule)
                        : NavigateSegments(ProfileTreeNode.From(exRule), segments, nextIndex);
                }
                return isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex);

            case MemberSelection.IncludeAll:
                if (node.ExtensionsByName.TryGetValue(extensionName, out ExtensionRule? allRule))
                {
                    return isLastSegment
                        ? ProfileTreeNode.From(allRule)
                        : NavigateSegments(ProfileTreeNode.From(allRule), segments, nextIndex);
                }
                return isLastSegment
                    ? ProfileTreeNode.IncludeAllDefault()
                    : NavigateSegments(ProfileTreeNode.IncludeAllDefault(), segments, nextIndex);

            default:
                return null;
        }
    }

    private static string StripArraySuffix(string segment) =>
        segment.EndsWith("[*]", StringComparison.Ordinal) ? segment[..^3] : segment;
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~ProfileTreeNavigatorTests"`
Expected: All tests PASS

- [ ] **Step 3: Commit**

```bash
git add src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileTreeNavigator.cs
git commit -m "[DMS-1115] Implement ProfileTreeNavigator for shared profile tree navigation"
```

---

## Task 4: Refactor SemanticIdentityCompatibilityValidator to Use ProfileTreeNavigator

Replace the private `ProfileTreeNode` and navigation methods in the validator with calls to the shared `ProfileTreeNavigator`.

**Files:**
- Modify: `src/dms/core/EdFi.DataManagementService.Core/Profile/SemanticIdentityCompatibilityValidator.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core/Profile/SemanticIdentityCompatibilityValidator.cs` — full file, understand what to keep vs replace
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileTreeNavigator.cs` — the new shared navigator

- [ ] **Step 1: Replace the validator's private navigation with ProfileTreeNavigator**

Replace the entire `SemanticIdentityCompatibilityValidator` with a version that:
1. Uses `ProfileTreeNavigator` for navigation instead of the private `ProfileTreeNode` struct and methods
2. Keeps the same public `Validate` signature and return type
3. Keeps the `GetHiddenSemanticIdentityMembers` and `ExtractTopLevelMember` helper methods (these are specific to C2's validation logic, not navigation)
4. Removes all private navigation code: the `ProfileTreeNode` struct, `FindCollectionInProfile`, `Navigate`, `NavigateIntermediateCollection`, `NavigateObject`, `NavigateExtension`, `LookupCollection`, `CollectionVisibility` enum, `CollectionLookupResult`, `StripArraySuffix`, and the static sentinel fields `_hidden` and `_visibleAllMembers`

The refactored validator should navigate to each collection scope's `ProfileTreeNode` using `ProfileTreeNavigator.Navigate(scope.JsonScope)`. If `Navigate` returns null, the scope is hidden. If it returns a node, use the node's `MemberSelection` and `ExplicitPropertyNames` to determine if semantic identity members are hidden.

```csharp
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
/// This validator consumes the compiled-scope adapter contract from C1, the
/// shared profile tree navigator, and the typed error contract from C8. It is a
/// pure function: callers provide compiled scope descriptors and a profile
/// definition, and receive back a list of structured failures (empty if the
/// profile is valid).
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

        var navigator = new ProfileTreeNavigator(writeContent);
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

            ProfileTreeNode? node = navigator.Navigate(scope.JsonScope);

            // null = hidden by ancestor IncludeOnly; no identity check needed
            if (node == null)
            {
                continue;
            }

            // IncludeAll = all members visible; no identity check needed
            if (node.Value.MemberSelection == MemberSelection.IncludeAll)
            {
                continue;
            }

            ImmutableArray<string> hiddenMembers = GetHiddenSemanticIdentityMembers(
                node.Value,
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
    //  Member visibility
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the subset of semantic identity paths that are hidden by the
    /// profile tree node's member selection.
    /// </summary>
    private static ImmutableArray<string> GetHiddenSemanticIdentityMembers(
        ProfileTreeNode node,
        ImmutableArray<string> semanticIdentityPaths
    )
    {
        ImmutableArray<string>.Builder? hidden = null;

        foreach (string path in semanticIdentityPaths)
        {
            string memberName = ExtractTopLevelMember(path);

            bool isHidden = node.MemberSelection switch
            {
                MemberSelection.IncludeOnly => !node.ExplicitPropertyNames.Contains(memberName),
                MemberSelection.ExcludeOnly => node.ExplicitPropertyNames.Contains(memberName),
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
}
```

- [ ] **Step 2: Run existing C2 tests to verify no regressions**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~SemanticIdentityCompatibilityValidatorTests"`
Expected: All existing tests PASS

- [ ] **Step 3: Run all profile tests**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~Profile"`
Expected: All tests PASS (including ProfileTreeNavigator tests)

- [ ] **Step 4: Commit**

```bash
git add src/dms/core/EdFi.DataManagementService.Core/Profile/SemanticIdentityCompatibilityValidator.cs
git commit -m "[DMS-1115] Refactor SemanticIdentityCompatibilityValidator to use shared ProfileTreeNavigator"
```

---

## Task 5: ProfileVisibilityClassifier — Tests

Write failing tests for the shared visibility primitive.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileVisibilityClassifierTests.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core.External/Profile/ProfileVisibilityKind.cs` — the enum we defined
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileContext.cs` — `CollectionItemFilter`, `FilterMode`

- [ ] **Step 1: Write `ProfileVisibilityClassifierTests.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileVisibilityClassifierTests
{
    /// <summary>
    /// Shared compiled scope catalog: StudentSchoolAssociation from delivery plan.
    /// </summary>
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
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
    /// IncludeOnly profile exposing root members + classPeriods, hiding calendarReference.
    /// </summary>
    protected static ContentTypeDefinition IncludeOnlyProfile =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
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
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    /// <summary>
    /// ExcludeOnly profile that excludes calendarReference.
    /// </summary>
    protected static ContentTypeDefinition ExcludeOnlyExcludingCalendar =>
        new(
            MemberSelection: MemberSelection.ExcludeOnly,
            Properties: [],
            Objects:
            [
                new ObjectRule(
                    Name: "calendarReference",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: null,
                    NestedObjects: null,
                    Collections: null,
                    Extensions: null
                ),
            ],
            Collections: [],
            Extensions: []
        );

    /// <summary>
    /// IncludeAll profile — everything visible.
    /// </summary>
    protected static ContentTypeDefinition IncludeAllProfile =>
        new(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [],
            Objects: [],
            Collections: [],
            Extensions: []
        );

    // -----------------------------------------------------------------------
    //  Scope-level visibility
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Profile_And_Visible_Scope_With_Data
        : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, SharedFixtureScopes);
            _result = classifier.ClassifyScope("$", JsonNode.Parse("{}")!);
        }

        [Test]
        public void It_classifies_as_VisiblePresent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    [TestFixture]
    public class Given_IncludeOnly_Profile_And_Visible_Scope_Without_Data
        : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // classPeriods is visible in IncludeOnly profile but we pass null for data
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, SharedFixtureScopes);
            _result = classifier.ClassifyScope("$.classPeriods[*]", null);
        }

        [Test]
        public void It_classifies_as_VisibleAbsent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisibleAbsent);
        }
    }

    [TestFixture]
    public class Given_IncludeOnly_Profile_And_Hidden_Scope : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, SharedFixtureScopes);
            // calendarReference is NOT in the IncludeOnly profile -> hidden
            _result = classifier.ClassifyScope(
                "$.calendarReference",
                JsonNode.Parse("""{"calendarCode":"CAL1"}""")
            );
        }

        [Test]
        public void It_classifies_as_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    [TestFixture]
    public class Given_ExcludeOnly_Profile_Excluding_A_Scope : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // ExcludeOnly with calendarReference explicitly listed -> hidden
            var excludeCalendarProfile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.ExcludeOnly,
                Properties: [new PropertyRule("calendarReference")],
                Objects: [],
                Collections: [],
                Extensions: []
            );
            var classifier = new ProfileVisibilityClassifier(excludeCalendarProfile, SharedFixtureScopes);
            _result = classifier.ClassifyScope(
                "$.calendarReference",
                JsonNode.Parse("""{"calendarCode":"CAL1"}""")
            );
        }

        [Test]
        public void It_classifies_as_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    [TestFixture]
    public class Given_ExcludeOnly_Profile_And_Non_Excluded_Scope_With_Data
        : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(
                ExcludeOnlyExcludingCalendar,
                SharedFixtureScopes
            );
            // Root is not excluded -> visible
            _result = classifier.ClassifyScope("$", JsonNode.Parse("{}")!);
        }

        [Test]
        public void It_classifies_as_VisiblePresent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    [TestFixture]
    public class Given_IncludeAll_Profile : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _rootResult;
        private ProfileVisibilityKind _calendarResult;
        private ProfileVisibilityKind _collectionResult;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(IncludeAllProfile, SharedFixtureScopes);
            _rootResult = classifier.ClassifyScope("$", JsonNode.Parse("{}")!);
            _calendarResult = classifier.ClassifyScope(
                "$.calendarReference",
                JsonNode.Parse("""{"calendarCode":"CAL1"}""")
            );
            _collectionResult = classifier.ClassifyScope("$.classPeriods[*]", null);
        }

        [Test]
        public void It_classifies_root_as_VisiblePresent()
        {
            _rootResult.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }

        [Test]
        public void It_classifies_calendar_as_VisiblePresent()
        {
            _calendarResult.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }

        [Test]
        public void It_classifies_absent_collection_as_VisibleAbsent()
        {
            _collectionResult.Should().Be(ProfileVisibilityKind.VisibleAbsent);
        }
    }

    [TestFixture]
    public class Given_Hidden_Parent_Scope : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            // Catalog with a child of calendarReference
            var scopesWithChild = new List<CompiledScopeDescriptor>(SharedFixtureScopes)
            {
                new(
                    JsonScope: "$.calendarReference.nested",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$.calendarReference",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["nestedField"]
                ),
            };

            // IncludeOnly profile does not include calendarReference -> child is also hidden
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, scopesWithChild);
            _result = classifier.ClassifyScope("$.calendarReference.nested", JsonNode.Parse("{}")!);
        }

        [Test]
        public void It_classifies_child_as_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    // -----------------------------------------------------------------------
    //  Extension scope visibility
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Extension_Scope_With_IncludeOnly_Parent_Not_Listed
        : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var scopesWithExt = new List<CompiledScopeDescriptor>(SharedFixtureScopes)
            {
                new(
                    JsonScope: "$._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["sampleField"]
                ),
            };

            // IncludeOnly root does not list the sample extension -> hidden
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, scopesWithExt);
            _result = classifier.ClassifyScope("$._ext.sample", JsonNode.Parse("{}")!);
        }

        [Test]
        public void It_classifies_as_Hidden()
        {
            _result.Should().Be(ProfileVisibilityKind.Hidden);
        }
    }

    [TestFixture]
    public class Given_Extension_Scope_With_IncludeAll_Parent : ProfileVisibilityClassifierTests
    {
        private ProfileVisibilityKind _result;

        [SetUp]
        public void Setup()
        {
            var scopesWithExt = new List<CompiledScopeDescriptor>(SharedFixtureScopes)
            {
                new(
                    JsonScope: "$._ext.sample",
                    ScopeKind: ScopeKind.NonCollection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: ["sampleField"]
                ),
            };

            var classifier = new ProfileVisibilityClassifier(IncludeAllProfile, scopesWithExt);
            _result = classifier.ClassifyScope("$._ext.sample", JsonNode.Parse("{}")!);
        }

        [Test]
        public void It_classifies_as_VisiblePresent()
        {
            _result.Should().Be(ProfileVisibilityKind.VisiblePresent);
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filtering
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_No_Item_Filter : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, SharedFixtureScopes);
            _result = classifier.PassesCollectionItemFilter(
                "$.classPeriods[*]",
                JsonNode.Parse("""{"classPeriodName":"Morning"}""")!
            );
        }

        [Test]
        public void It_passes()
        {
            _result.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Include_Filter_And_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.IncludeOnly,
                            ["uri://ed-fi.org/AddressType#Physical"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var classifier = new ProfileVisibilityClassifier(profileWithFilter, scopes);
            _result = classifier.PassesCollectionItemFilter(
                "$.addresses[*]",
                JsonNode.Parse(
                    """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Physical","city":"Austin"}"""
                )!
            );
        }

        [Test]
        public void It_passes()
        {
            _result.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Include_Filter_And_Non_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.IncludeOnly,
                            ["uri://ed-fi.org/AddressType#Physical"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var classifier = new ProfileVisibilityClassifier(profileWithFilter, scopes);
            _result = classifier.PassesCollectionItemFilter(
                "$.addresses[*]",
                JsonNode.Parse(
                    """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Mailing","city":"Austin"}"""
                )!
            );
        }

        [Test]
        public void It_fails()
        {
            _result.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_Exclude_Filter_And_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.ExcludeOnly,
                            ["uri://ed-fi.org/AddressType#Mailing"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var classifier = new ProfileVisibilityClassifier(profileWithFilter, scopes);
            _result = classifier.PassesCollectionItemFilter(
                "$.addresses[*]",
                JsonNode.Parse(
                    """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Mailing","city":"Austin"}"""
                )!
            );
        }

        [Test]
        public void It_fails()
        {
            _result.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_Exclude_Filter_And_Non_Matching_Item : ProfileVisibilityClassifierTests
    {
        private bool _result;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.ExcludeOnly,
                            ["uri://ed-fi.org/AddressType#Mailing"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var classifier = new ProfileVisibilityClassifier(profileWithFilter, scopes);
            _result = classifier.PassesCollectionItemFilter(
                "$.addresses[*]",
                JsonNode.Parse(
                    """{"addressTypeDescriptor":"uri://ed-fi.org/AddressType#Physical","city":"Austin"}"""
                )!
            );
        }

        [Test]
        public void It_passes()
        {
            _result.Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  Member filtering
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeOnly_Scope_Member_Filter : ProfileVisibilityClassifierTests
    {
        private ScopeMemberFilter _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(IncludeOnlyProfile, SharedFixtureScopes);
            _result = classifier.GetMemberFilter("$.classPeriods[*]");
        }

        [Test]
        public void It_returns_IncludeOnly_mode()
        {
            _result.Mode.Should().Be(MemberSelection.IncludeOnly);
        }

        [Test]
        public void It_returns_included_property_names()
        {
            _result.ExplicitNames.Should().BeEquivalentTo(["classPeriodName"]);
        }
    }

    [TestFixture]
    public class Given_IncludeAll_Scope_Member_Filter : ProfileVisibilityClassifierTests
    {
        private ScopeMemberFilter _result;

        [SetUp]
        public void Setup()
        {
            var classifier = new ProfileVisibilityClassifier(IncludeAllProfile, SharedFixtureScopes);
            _result = classifier.GetMemberFilter("$");
        }

        [Test]
        public void It_returns_IncludeAll_mode()
        {
            _result.Mode.Should().Be(MemberSelection.IncludeAll);
        }

        [Test]
        public void It_returns_empty_explicit_names()
        {
            _result.ExplicitNames.Should().BeEmpty();
        }
    }
}
```

- [ ] **Step 2: Verify tests fail (ProfileVisibilityClassifier does not exist yet)**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj`
Expected: Build FAILS — `ProfileVisibilityClassifier` and `ScopeMemberFilter` not found

- [ ] **Step 3: Commit failing tests**

```bash
git add src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileVisibilityClassifierTests.cs
git commit -m "[DMS-1115] Add failing tests for ProfileVisibilityClassifier"
```

---

## Task 6: ProfileVisibilityClassifier — Implementation

Implement the shared visibility primitive with pre-computed scope cache.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileVisibilityClassifier.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileTreeNavigator.cs` — navigator to use
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileContext.cs` — `CollectionItemFilter`, `FilterMode`
- `src/dms/core/EdFi.DataManagementService.Core.External/Profile/CompiledScopeTypes.cs` — `CompiledScopeDescriptor`

- [ ] **Step 1: Create `ProfileVisibilityClassifier.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Member filter result for a scope — tells the shaper what to include or exclude.
/// </summary>
/// <param name="Mode">The member selection mode at this scope.</param>
/// <param name="ExplicitNames">
/// For IncludeOnly: the set of included property names.
/// For ExcludeOnly: the set of excluded property names.
/// For IncludeAll: empty set.
/// </param>
public readonly record struct ScopeMemberFilter(
    MemberSelection Mode,
    IReadOnlySet<string> ExplicitNames
);

/// <summary>
/// Shared reusable visibility primitive consumed by C3 (request side), C5
/// (stored-side existence lookup), and C6 (stored-state projection). Instance
/// class constructed with profile + scope catalog; pre-computes a visibility
/// lookup cache keyed by JsonScope.
/// </summary>
public sealed class ProfileVisibilityClassifier
{
    private readonly ProfileTreeNavigator _navigator;
    private readonly Dictionary<string, CachedScopeEntry> _cache;
    private readonly Dictionary<string, ScopeKind> _scopeKinds;

    /// <summary>
    /// Pre-computed visibility and member filter for a single compiled scope.
    /// </summary>
    private sealed record CachedScopeEntry(
        bool IsHidden,
        ProfileTreeNode? Node,
        CollectionItemFilter? ItemFilter
    );

    public ProfileVisibilityClassifier(
        ContentTypeDefinition writeContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        ArgumentNullException.ThrowIfNull(writeContentType);
        ArgumentNullException.ThrowIfNull(scopeCatalog);

        _navigator = new ProfileTreeNavigator(writeContentType);
        _cache = new Dictionary<string, CachedScopeEntry>(scopeCatalog.Count);
        _scopeKinds = new Dictionary<string, ScopeKind>(scopeCatalog.Count);

        foreach (CompiledScopeDescriptor scope in scopeCatalog)
        {
            _scopeKinds[scope.JsonScope] = scope.ScopeKind;
            ProfileTreeNode? node = _navigator.Navigate(scope.JsonScope);
            bool isHidden = node == null;

            CollectionItemFilter? itemFilter = null;
            if (!isHidden && scope.ScopeKind == ScopeKind.Collection)
            {
                itemFilter = ResolveItemFilter(node!.Value, scope.JsonScope);
            }

            _cache[scope.JsonScope] = new CachedScopeEntry(isHidden, node, itemFilter);
        }
    }

    /// <summary>
    /// Classify scope visibility: Hidden, VisiblePresent, or VisibleAbsent.
    /// </summary>
    /// <param name="jsonScope">Compiled JsonScope identifier.</param>
    /// <param name="scopeData">The JsonNode at this scope's position in the document, or null if absent.</param>
    public ProfileVisibilityKind ClassifyScope(string jsonScope, JsonNode? scopeData)
    {
        if (!_cache.TryGetValue(jsonScope, out CachedScopeEntry? entry))
        {
            throw new InvalidOperationException(
                $"JsonScope '{jsonScope}' is not in the pre-computed scope catalog."
            );
        }

        if (entry.IsHidden)
        {
            return ProfileVisibilityKind.Hidden;
        }

        return scopeData != null ? ProfileVisibilityKind.VisiblePresent : ProfileVisibilityKind.VisibleAbsent;
    }

    /// <summary>
    /// Evaluate collection item value filter. Returns true if the item passes
    /// (or no filter is defined).
    /// </summary>
    /// <param name="jsonScope">Compiled JsonScope of the collection.</param>
    /// <param name="collectionItem">The concrete JSON collection item to evaluate.</param>
    public bool PassesCollectionItemFilter(string jsonScope, JsonNode collectionItem)
    {
        ArgumentNullException.ThrowIfNull(collectionItem);

        if (!_cache.TryGetValue(jsonScope, out CachedScopeEntry? entry))
        {
            throw new InvalidOperationException(
                $"JsonScope '{jsonScope}' is not in the pre-computed scope catalog."
            );
        }

        if (entry.ItemFilter == null)
        {
            return true;
        }

        string? propertyValue = collectionItem[entry.ItemFilter.PropertyName]?.GetValue<string>();
        if (propertyValue == null)
        {
            // No value for filter property — IncludeOnly rejects, ExcludeOnly accepts
            return entry.ItemFilter.FilterMode == FilterMode.ExcludeOnly;
        }

        bool matchesFilter = entry.ItemFilter.Values.Contains(propertyValue);

        return entry.ItemFilter.FilterMode switch
        {
            FilterMode.IncludeOnly => matchesFilter,
            FilterMode.ExcludeOnly => !matchesFilter,
            _ => true,
        };
    }

    /// <summary>
    /// Get the member filter for a scope — tells the shaper what to include or exclude.
    /// </summary>
    /// <param name="jsonScope">Compiled JsonScope identifier.</param>
    public ScopeMemberFilter GetMemberFilter(string jsonScope)
    {
        if (!_cache.TryGetValue(jsonScope, out CachedScopeEntry? entry))
        {
            throw new InvalidOperationException(
                $"JsonScope '{jsonScope}' is not in the pre-computed scope catalog."
            );
        }

        if (entry.IsHidden || entry.Node == null)
        {
            // Hidden scopes have no meaningful member filter; caller should check
            // ClassifyScope first. Return IncludeAll with empty set as a safe default.
            return new ScopeMemberFilter(MemberSelection.IncludeAll, new HashSet<string>());
        }

        return new ScopeMemberFilter(entry.Node.Value.MemberSelection, entry.Node.Value.ExplicitPropertyNames);
    }

    /// <summary>
    /// All JsonScope keys in the pre-computed cache. Used by the shaper to emit
    /// scope states for scopes not encountered during the JSON walk.
    /// </summary>
    public IEnumerable<string> AllScopeJsonScopes => _cache.Keys;

    /// <summary>
    /// Returns the ScopeKind for a given JsonScope from the scope catalog.
    /// </summary>
    public ScopeKind GetScopeKind(string jsonScope) => _scopeKinds[jsonScope];

    /// <summary>
    /// Resolves the <see cref="CollectionItemFilter"/> for a collection scope by
    /// navigating from the parent scope to the collection rule.
    /// </summary>
    private CollectionItemFilter? ResolveItemFilter(ProfileTreeNode node, string jsonScope)
    {
        // The node at the collection's JsonScope represents the collection rule itself.
        // We need to find the CollectionRule from the parent to get the ItemFilter.
        // Navigate to the parent, then look up the collection by name.
        string[] segments = jsonScope.Split('.');
        string lastSegment = segments[^1];
        string collectionName = lastSegment.EndsWith("[*]", StringComparison.Ordinal)
            ? lastSegment[..^3]
            : lastSegment;

        // Build parent JsonScope
        if (segments.Length <= 1)
        {
            return null;
        }

        // Check if the segment before last is _ext (extension collection)
        bool isExtensionChild =
            segments.Length >= 3 && segments[^3] == "_ext";

        string parentJsonScope;
        if (isExtensionChild)
        {
            // For extension collections like "$._ext.sample.extActivities[*]",
            // parent is "$._ext.sample"
            parentJsonScope = string.Join('.', segments[..^1]);
        }
        else
        {
            parentJsonScope = string.Join('.', segments[..^1]);
        }

        ProfileTreeNode? parentNode = _navigator.Navigate(parentJsonScope);
        if (parentNode == null)
        {
            return null;
        }

        if (parentNode.Value.CollectionsByName.TryGetValue(collectionName, out CollectionRule? rule))
        {
            return rule.ItemFilter;
        }

        return null;
    }
}
```

- [ ] **Step 2: Run classifier tests**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~ProfileVisibilityClassifierTests"`
Expected: All tests PASS

- [ ] **Step 3: Run all profile tests (including navigator and C2 validator)**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~Profile"`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileVisibilityClassifier.cs
git commit -m "[DMS-1115] Implement ProfileVisibilityClassifier shared visibility primitive"
```

---

## Task 7: WritableRequestShaper — Tests

Write failing tests for the request-side shaper. Uses the shared reference fixture (StudentSchoolAssociation + RestrictedAssociation-Write) from the delivery plan.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/WritableRequestShaperTests.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core/Profile/AddressDerivationEngine.cs` — address engine API
- `src/dms/core/EdFi.DataManagementService.Core.External/Profile/AddressTypes.cs` — address types
- `src/dms/core/EdFi.DataManagementService.Core.External/Profile/ProfileFailure.cs` — `WritableProfileValidationFailure` and `ForbiddenSubmittedDataWritableProfileValidationFailure`

- [ ] **Step 1: Write `WritableRequestShaperTests.cs`**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class WritableRequestShaperTests
{
    /// <summary>
    /// Shared compiled scope catalog: StudentSchoolAssociation from delivery plan.
    /// </summary>
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
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
    /// RestrictedAssociation-Write profile from the delivery plan.
    /// Exposes: studentReference, schoolReference, entryDate, classPeriods.classPeriodName
    /// Hides: entryTypeDescriptor, calendarReference, classPeriods.officialAttendancePeriod
    /// </summary>
    protected static ContentTypeDefinition RestrictedWriteProfile =>
        new(
            MemberSelection: MemberSelection.IncludeOnly,
            Properties:
            [
                new PropertyRule("studentReference"),
                new PropertyRule("schoolReference"),
                new PropertyRule("entryDate"),
            ],
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
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions: []
        );

    protected static WritableRequestShaper BuildShaper(
        ContentTypeDefinition writeContent,
        IReadOnlyList<CompiledScopeDescriptor> scopes
    )
    {
        var classifier = new ProfileVisibilityClassifier(writeContent, scopes);
        var addressEngine = new AddressDerivationEngine(scopes);
        return new WritableRequestShaper(classifier, addressEngine);
    }

    // -----------------------------------------------------------------------
    //  Shared reference fixture: full delivery plan example
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Shared_Reference_Fixture_Request : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var shaper = BuildShaper(RestrictedWriteProfile, SharedFixtureScopes);

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                  "studentReference": { "studentUniqueId": "S001" },
                  "schoolReference": { "schoolId": 100 },
                  "entryDate": "2025-09-01",
                  "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Transfer",
                  "calendarReference": { "calendarCode": "CAL1", "calendarTypeDescriptor": "uri://..." },
                  "classPeriods": [
                    { "classPeriodName": "Morning", "officialAttendancePeriod": true }
                  ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_produces_shaped_body_without_hidden_root_members()
        {
            _result.WritableRequestBody["entryTypeDescriptor"].Should().BeNull();
        }

        [Test]
        public void It_produces_shaped_body_without_hidden_scope()
        {
            _result.WritableRequestBody["calendarReference"].Should().BeNull();
        }

        [Test]
        public void It_produces_shaped_body_with_visible_root_members()
        {
            _result.WritableRequestBody["studentReference"].Should().NotBeNull();
            _result.WritableRequestBody["schoolReference"].Should().NotBeNull();
            _result.WritableRequestBody["entryDate"].Should().NotBeNull();
        }

        [Test]
        public void It_produces_shaped_collection_items_without_hidden_members()
        {
            var classPeriods = _result.WritableRequestBody["classPeriods"]!.AsArray();
            classPeriods.Should().HaveCount(1);
            classPeriods[0]!["classPeriodName"]!.GetValue<string>().Should().Be("Morning");
            classPeriods[0]!["officialAttendancePeriod"].Should().BeNull();
        }

        [Test]
        public void It_emits_root_scope_state_as_VisiblePresent()
        {
            _result.RequestScopeStates.Should().Contain(s =>
                s.Address.JsonScope == "$" && s.Visibility == ProfileVisibilityKind.VisiblePresent
            );
        }

        [Test]
        public void It_emits_calendar_scope_state_as_Hidden()
        {
            _result.RequestScopeStates.Should().Contain(s =>
                s.Address.JsonScope == "$.calendarReference"
                && s.Visibility == ProfileVisibilityKind.Hidden
            );
        }

        [Test]
        public void It_emits_all_Creatable_flags_as_false()
        {
            _result.RequestScopeStates.Should().OnlyContain(s => s.Creatable == false);
            _result.VisibleRequestCollectionItems.Should().OnlyContain(i => i.Creatable == false);
        }

        [Test]
        public void It_emits_one_visible_collection_item()
        {
            _result.VisibleRequestCollectionItems.Should().HaveCount(1);
            _result.VisibleRequestCollectionItems[0].Address.JsonScope
                .Should()
                .Be("$.classPeriods[*]");
        }

        [Test]
        public void It_has_no_validation_failures()
        {
            _result.ValidationFailures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  VisibleAbsent scope
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Absent_Visible_Scope : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // IncludeAll profile so calendarReference is visible
            var includeAllProfile = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections: [],
                Extensions: []
            );

            var shaper = BuildShaper(includeAllProfile, SharedFixtureScopes);

            // Request has no calendarReference -> VisibleAbsent
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                  "studentReference": { "studentUniqueId": "S001" },
                  "schoolReference": { "schoolId": 100 },
                  "entryDate": "2025-09-01"
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_classifies_absent_scope_as_VisibleAbsent()
        {
            _result.RequestScopeStates.Should().Contain(s =>
                s.Address.JsonScope == "$.calendarReference"
                && s.Visibility == ProfileVisibilityKind.VisibleAbsent
            );
        }
    }

    // -----------------------------------------------------------------------
    //  Collection item value filter violations
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Item_Failing_Value_Filter : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.IncludeOnly,
                            ["uri://ed-fi.org/AddressType#Physical"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var shaper = BuildShaper(profileWithFilter, scopes);

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                  "addresses": [
                    { "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing", "city": "Austin" }
                  ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_collects_a_validation_failure()
        {
            _result.ValidationFailures.Should().HaveCount(1);
        }

        [Test]
        public void It_excludes_the_failing_item_from_output()
        {
            var addresses = _result.WritableRequestBody["addresses"];
            // Either null or empty array
            if (addresses != null)
            {
                addresses.AsArray().Should().BeEmpty();
            }
        }

        [Test]
        public void It_does_not_emit_a_visible_collection_item()
        {
            _result.VisibleRequestCollectionItems.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Multiple_Failing_Items : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.IncludeOnly,
                            ["uri://ed-fi.org/AddressType#Physical"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var shaper = BuildShaper(profileWithFilter, scopes);

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                  "addresses": [
                    { "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Mailing", "city": "Austin" },
                    { "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Temporary", "city": "Dallas" }
                  ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_collects_all_validation_failures()
        {
            _result.ValidationFailures.Should().HaveCount(2);
        }
    }

    [TestFixture]
    public class Given_Items_Passing_Value_Filter : WritableRequestShaperTests
    {
        private WritableRequestShapingResult _result = null!;

        [SetUp]
        public void Setup()
        {
            var profileWithFilter = new ContentTypeDefinition(
                MemberSelection: MemberSelection.IncludeAll,
                Properties: [],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "addresses",
                        MemberSelection: MemberSelection.IncludeAll,
                        LogicalSchema: null,
                        Properties: null,
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: new CollectionItemFilter(
                            "addressTypeDescriptor",
                            FilterMode.IncludeOnly,
                            ["uri://ed-fi.org/AddressType#Physical"]
                        )
                    ),
                ],
                Extensions: []
            );

            var scopes = new List<CompiledScopeDescriptor>
            {
                new(
                    JsonScope: "$",
                    ScopeKind: ScopeKind.Root,
                    ImmediateParentJsonScope: null,
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: [],
                    CanonicalScopeRelativeMemberPaths: []
                ),
                new(
                    JsonScope: "$.addresses[*]",
                    ScopeKind: ScopeKind.Collection,
                    ImmediateParentJsonScope: "$",
                    CollectionAncestorsInOrder: [],
                    SemanticIdentityRelativePathsInOrder: ["addressTypeDescriptor"],
                    CanonicalScopeRelativeMemberPaths:
                        ["addressTypeDescriptor", "streetNumberName", "city"]
                ),
            };

            var shaper = BuildShaper(profileWithFilter, scopes);

            JsonNode requestBody = JsonNode.Parse(
                """
                {
                  "addresses": [
                    { "addressTypeDescriptor": "uri://ed-fi.org/AddressType#Physical", "city": "Austin" }
                  ]
                }
                """
            )!;

            _result = shaper.Shape(requestBody);
        }

        [Test]
        public void It_has_no_validation_failures()
        {
            _result.ValidationFailures.Should().BeEmpty();
        }

        [Test]
        public void It_includes_the_item_in_output()
        {
            _result.WritableRequestBody["addresses"]!.AsArray().Should().HaveCount(1);
        }

        [Test]
        public void It_emits_a_visible_collection_item()
        {
            _result.VisibleRequestCollectionItems.Should().HaveCount(1);
        }
    }
}
```

- [ ] **Step 2: Verify tests fail (WritableRequestShaper does not exist yet)**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj`
Expected: Build FAILS — `WritableRequestShaper` and `WritableRequestShapingResult` not found

- [ ] **Step 3: Commit failing tests**

```bash
git add src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/WritableRequestShaperTests.cs
git commit -m "[DMS-1115] Add failing tests for WritableRequestShaper"
```

---

## Task 8: WritableRequestShaper — Implementation

Implement the request-side shaping engine with single-pass walk.

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core/Profile/WritableRequestShaper.cs`

**Reference files to read first:**
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileVisibilityClassifier.cs` — classifier API
- `src/dms/core/EdFi.DataManagementService.Core/Profile/AddressDerivationEngine.cs` — address derivation API
- `src/dms/core/EdFi.DataManagementService.Core/Profile/AncestorItemContext.cs` — ancestor context type
- `src/dms/core/EdFi.DataManagementService.Core.External/Profile/ProfileFailure.cs` — search for `ForbiddenSubmittedData` factory (line ~821) to see its signature

- [ ] **Step 1: Create `WritableRequestShaper.cs`**

**Important implementation note:** The code below includes a `HashSet<string> emittedScopes` parameter that is threaded through all internal walk methods (`ShapeScope`, `ShapeNonCollectionChild`, `ShapeCollection`, `ShapeObjectMembers`, `ShapeExtensions`). Every method that emits a `RequestScopeState` must also call `emittedScopes.Add(jsonScope)`. After the walk, `EmitMissingScopeStates` uses `classifier.AllScopeJsonScopes` and `classifier.GetScopeKind` to emit states for non-collection scopes not encountered during the walk (hidden or absent scopes). Thread `emittedScopes` through all internal method signatures that accept the `scopeStates` list.

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Result of request-side writable profile shaping.
/// </summary>
/// <param name="WritableRequestBody">Request JSON after profile shaping (hidden members removed).</param>
/// <param name="RequestScopeStates">Per non-collection scope: address, visibility, Creatable=false.</param>
/// <param name="VisibleRequestCollectionItems">Per visible collection item: address, Creatable=false.</param>
/// <param name="ValidationFailures">Category-3 failures for items failing value filters.</param>
public sealed record WritableRequestShapingResult(
    JsonNode WritableRequestBody,
    ImmutableArray<RequestScopeState> RequestScopeStates,
    ImmutableArray<VisibleRequestCollectionItem> VisibleRequestCollectionItems,
    ImmutableArray<WritableProfileValidationFailure> ValidationFailures
);

/// <summary>
/// Request-side shaping engine. Walks the request body once, building shaped
/// JSON output while emitting scope states, collection items, and validation
/// failures. Consumes <see cref="ProfileVisibilityClassifier"/> and
/// <see cref="AddressDerivationEngine"/>.
/// </summary>
public sealed class WritableRequestShaper(
    ProfileVisibilityClassifier classifier,
    AddressDerivationEngine addressEngine
)
{
    /// <summary>
    /// Shape the request body according to the writable profile.
    /// </summary>
    /// <param name="requestBody">The canonicalized request body.</param>
    /// <returns>Shaped result with filtered JSON, scope states, collection items, and failures.</returns>
    public WritableRequestShapingResult Shape(JsonNode requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var scopeStates = new List<RequestScopeState>();
        var collectionItems = new List<VisibleRequestCollectionItem>();
        var failures = new List<WritableProfileValidationFailure>();
        var ancestorItems = new List<AncestorItemContext>();
        var emittedScopes = new HashSet<string>();

        JsonObject shapedRoot = ShapeScope(
            "$",
            requestBody.AsObject(),
            ancestorItems,
            scopeStates,
            collectionItems,
            failures,
            emittedScopes
        );

        // Emit scope states for non-collection scopes not encountered during walk
        // (hidden or absent scopes with no data in the request)
        EmitMissingScopeStates(scopeStates, ancestorItems, emittedScopes);

        return new WritableRequestShapingResult(
            shapedRoot,
            [.. scopeStates],
            [.. collectionItems],
            [.. failures]
        );
    }

    /// <summary>
    /// Emits RequestScopeState entries for non-collection scopes that were not
    /// encountered during the JSON walk (hidden or absent scopes).
    /// </summary>
    private void EmitMissingScopeStates(
        List<RequestScopeState> scopeStates,
        List<AncestorItemContext> ancestorItems,
        HashSet<string> emittedScopes
    )
    {
        foreach (string jsonScope in classifier.AllScopeJsonScopes)
        {
            if (emittedScopes.Contains(jsonScope))
            {
                continue;
            }

            // Only emit for non-collection scopes (Root and NonCollection)
            if (classifier.GetScopeKind(jsonScope) == ScopeKind.Collection)
            {
                continue;
            }

            var visibility = classifier.ClassifyScope(jsonScope, null);
            var address = addressEngine.DeriveScopeInstanceAddress(jsonScope, ancestorItems);
            scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
        }
    }

    private JsonObject ShapeScope(
        string jsonScope,
        JsonObject sourceObject,
        List<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> failures,
        HashSet<string> emittedScopes
    )
    {
        // Emit RequestScopeState for non-collection scopes
        var visibility = classifier.ClassifyScope(jsonScope, sourceObject);
        var address = addressEngine.DeriveScopeInstanceAddress(jsonScope, ancestorItems);
        scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
        emittedScopes.Add(jsonScope);

        var memberFilter = classifier.GetMemberFilter(jsonScope);
        var shapedObject = new JsonObject();

        foreach (var property in sourceObject)
        {
            string memberName = property.Key;

            // Handle _ext specially
            if (memberName == "_ext")
            {
                JsonObject? shapedExt = ShapeExtensions(
                    jsonScope,
                    property.Value?.AsObject(),
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                if (shapedExt != null && shapedExt.Count > 0)
                {
                    shapedObject["_ext"] = shapedExt;
                }
                continue;
            }

            if (!IsMemberVisible(memberFilter, memberName))
            {
                continue;
            }

            // Check if this member is a child non-collection scope
            string childScopeKey = $"{jsonScope}.{memberName}";
            if (IsNonCollectionScope(childScopeKey))
            {
                ShapeNonCollectionChild(
                    childScopeKey,
                    property.Value,
                    shapedObject,
                    memberName,
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                continue;
            }

            // Check if this member is a collection scope
            string collectionScopeKey = $"{jsonScope}.{memberName}[*]";
            if (IsCollectionScope(collectionScopeKey))
            {
                ShapeCollection(
                    collectionScopeKey,
                    property.Value,
                    shapedObject,
                    memberName,
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                continue;
            }

            // Scalar/property — copy to output
            shapedObject[memberName] = property.Value?.DeepClone();
        }

        return shapedObject;
    }

    private void ShapeNonCollectionChild(
        string childJsonScope,
        JsonNode? childData,
        JsonObject parentOutput,
        string memberName,
        List<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> failures
    )
    {
        var visibility = classifier.ClassifyScope(childJsonScope, childData);
        var address = addressEngine.DeriveScopeInstanceAddress(childJsonScope, ancestorItems);
        scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
        // Note: also add to emittedScopes here when threading the HashSet through

        if (visibility == ProfileVisibilityKind.VisiblePresent && childData != null)
        {
            JsonObject shapedChild = ShapeObjectMembers(
                childJsonScope,
                childData.AsObject(),
                ancestorItems,
                scopeStates,
                collectionItems,
                failures
            );
            parentOutput[memberName] = shapedChild;
        }
    }

    private JsonObject ShapeObjectMembers(
        string jsonScope,
        JsonObject sourceObject,
        List<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> failures
    )
    {
        var memberFilter = classifier.GetMemberFilter(jsonScope);
        var shapedObject = new JsonObject();

        foreach (var property in sourceObject)
        {
            string memberName = property.Key;

            if (memberName == "_ext")
            {
                JsonObject? shapedExt = ShapeExtensions(
                    jsonScope,
                    property.Value?.AsObject(),
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                if (shapedExt != null && shapedExt.Count > 0)
                {
                    shapedObject["_ext"] = shapedExt;
                }
                continue;
            }

            if (!IsMemberVisible(memberFilter, memberName))
            {
                continue;
            }

            // Check nested non-collection child
            string childScopeKey = $"{jsonScope}.{memberName}";
            if (IsNonCollectionScope(childScopeKey))
            {
                ShapeNonCollectionChild(
                    childScopeKey,
                    property.Value,
                    shapedObject,
                    memberName,
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                continue;
            }

            // Check nested collection
            string collectionScopeKey = $"{jsonScope}.{memberName}[*]";
            if (IsCollectionScope(collectionScopeKey))
            {
                ShapeCollection(
                    collectionScopeKey,
                    property.Value,
                    shapedObject,
                    memberName,
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                continue;
            }

            shapedObject[memberName] = property.Value?.DeepClone();
        }

        return shapedObject;
    }

    private void ShapeCollection(
        string collectionJsonScope,
        JsonNode? collectionData,
        JsonObject parentOutput,
        string memberName,
        List<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> failures
    )
    {
        var visibility = classifier.ClassifyScope(collectionJsonScope, collectionData);
        if (visibility == ProfileVisibilityKind.Hidden)
        {
            return;
        }

        if (
            visibility == ProfileVisibilityKind.VisibleAbsent
            || collectionData == null
        )
        {
            return;
        }

        var sourceArray = collectionData.AsArray();
        var shapedArray = new JsonArray();
        var memberFilter = classifier.GetMemberFilter(collectionJsonScope);

        for (int i = 0; i < sourceArray.Count; i++)
        {
            JsonNode item = sourceArray[i]!;

            // Check value filter
            if (!classifier.PassesCollectionItemFilter(collectionJsonScope, item))
            {
                failures.Add(
                    ProfileFailures.ForbiddenSubmittedData(
                        profileName: "unknown",
                        resourceName: "unknown",
                        method: "unknown",
                        operation: "unknown",
                        jsonScope: collectionJsonScope,
                        scopeKind: ScopeKind.Collection,
                        requestJsonPaths: [$"{collectionJsonScope}[{i}]"],
                        forbiddenCanonicalMemberPaths: []
                    )
                );
                continue;
            }

            // Derive address
            var rowAddress = addressEngine.DeriveCollectionRowAddress(
                collectionJsonScope,
                item,
                ancestorItems
            );
            collectionItems.Add(new VisibleRequestCollectionItem(rowAddress, Creatable: false));

            // Build shaped item
            var itemObject = item.AsObject();
            var shapedItem = new JsonObject();

            // Push ancestor context for nested scopes
            ancestorItems.Add(new AncestorItemContext(collectionJsonScope, item));

            foreach (var property in itemObject)
            {
                string propName = property.Key;

                if (propName == "_ext")
                {
                    JsonObject? shapedExt = ShapeExtensions(
                        collectionJsonScope,
                        property.Value?.AsObject(),
                        ancestorItems,
                        scopeStates,
                        collectionItems,
                        failures
                    );
                    if (shapedExt != null && shapedExt.Count > 0)
                    {
                        shapedItem["_ext"] = shapedExt;
                    }
                    continue;
                }

                if (!IsMemberVisible(memberFilter, propName))
                {
                    continue;
                }

                // Check nested non-collection scope within collection item
                string childScopeKey = $"{collectionJsonScope}.{propName}";
                if (IsNonCollectionScope(childScopeKey))
                {
                    ShapeNonCollectionChild(
                        childScopeKey,
                        property.Value,
                        shapedItem,
                        propName,
                        ancestorItems,
                        scopeStates,
                        collectionItems,
                        failures
                    );
                    continue;
                }

                // Check nested collection within collection item
                string nestedCollectionKey = $"{collectionJsonScope}.{propName}[*]";
                if (IsCollectionScope(nestedCollectionKey))
                {
                    ShapeCollection(
                        nestedCollectionKey,
                        property.Value,
                        shapedItem,
                        propName,
                        ancestorItems,
                        scopeStates,
                        collectionItems,
                        failures
                    );
                    continue;
                }

                shapedItem[propName] = property.Value?.DeepClone();
            }

            // Pop ancestor context
            ancestorItems.RemoveAt(ancestorItems.Count - 1);

            shapedArray.Add(shapedItem);
        }

        if (shapedArray.Count > 0)
        {
            parentOutput[memberName] = shapedArray;
        }
    }

    private JsonObject? ShapeExtensions(
        string parentJsonScope,
        JsonObject? extObject,
        List<AncestorItemContext> ancestorItems,
        List<RequestScopeState> scopeStates,
        List<VisibleRequestCollectionItem> collectionItems,
        List<WritableProfileValidationFailure> failures
    )
    {
        if (extObject == null)
        {
            return null;
        }

        var shapedExt = new JsonObject();

        foreach (var extEntry in extObject)
        {
            string extName = extEntry.Key;
            string extJsonScope = $"{parentJsonScope}._ext.{extName}";

            if (!IsNonCollectionScope(extJsonScope))
            {
                // Extension scope not in catalog — skip
                continue;
            }

            var visibility = classifier.ClassifyScope(extJsonScope, extEntry.Value);
            var address = addressEngine.DeriveScopeInstanceAddress(extJsonScope, ancestorItems);
            scopeStates.Add(new RequestScopeState(address, visibility, Creatable: false));
            // Note: also add to emittedScopes here when threading the HashSet through

            if (visibility == ProfileVisibilityKind.VisiblePresent && extEntry.Value != null)
            {
                JsonObject shapedExtChild = ShapeObjectMembers(
                    extJsonScope,
                    extEntry.Value.AsObject(),
                    ancestorItems,
                    scopeStates,
                    collectionItems,
                    failures
                );
                shapedExt[extName] = shapedExtChild;
            }
        }

        return shapedExt;
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private bool IsNonCollectionScope(string jsonScope)
    {
        try
        {
            classifier.ClassifyScope(jsonScope, null);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsCollectionScope(string jsonScope)
    {
        try
        {
            classifier.ClassifyScope(jsonScope, null);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsMemberVisible(ScopeMemberFilter filter, string memberName)
    {
        return filter.Mode switch
        {
            MemberSelection.IncludeOnly => filter.ExplicitNames.Contains(memberName),
            MemberSelection.ExcludeOnly => !filter.ExplicitNames.Contains(memberName),
            MemberSelection.IncludeAll => true,
            _ => true,
        };
    }
}
```

- [ ] **Step 2: Run shaper tests**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~WritableRequestShaperTests"`
Expected: All tests PASS

- [ ] **Step 3: Run all profile tests**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~Profile"`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/dms/core/EdFi.DataManagementService.Core/Profile/WritableRequestShaper.cs
git commit -m "[DMS-1115] Implement WritableRequestShaper for request-side profile shaping"
```

---

## Task 9: Full Build + All Tests

Verify everything compiles and all tests pass across the solution.

- [ ] **Step 1: Build the full solution**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj`
Expected: Build succeeded

- [ ] **Step 2: Run all unit tests in the test project**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj`
Expected: All tests PASS

- [ ] **Step 3: Format code**

Run: `dotnet csharpier format src/dms/core/`
Expected: No formatting changes (or apply and commit if needed)

- [ ] **Step 4: Commit any formatting fixes**

```bash
git add -u src/dms/core/
git commit -m "[DMS-1115] Apply CSharpier formatting"
```
