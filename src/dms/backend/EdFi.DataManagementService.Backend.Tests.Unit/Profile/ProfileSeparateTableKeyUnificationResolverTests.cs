// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_SeparateTableResolver_instance_aware_overload_for_CollectionExtensionScope_table_kind
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusSeparateTablePlanWithNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.CollectionExtensionScope
        );
        var separatePlan = plan.TablePlansInDependencyOrder[1];
        var scopeAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var requestScope = new RequestScopeState(
            scopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var context = BuildSeparateTableResolverContext(
            plan,
            profileRequest: CreateRequest(scopeStates: requestScope)
        );
        var row = new FlattenedWriteValue[separatePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }

        try
        {
            new ProfileSeparateTableKeyUnificationResolver().Resolve(
                separatePlan,
                context,
                scopeAddress,
                requestScope,
                storedScope: null,
                row,
                ImmutableHashSet<int>.Empty
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_does_not_throw() => _thrown.Should().BeNull();
}

[TestFixture]
public class Given_SeparateTableResolver_rejects_unsupported_table_kind
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusSeparateTableWithKeyUnificationNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.Root
        );
        var separatePlan = plan.TablePlansInDependencyOrder[1];
        var scopeAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var requestScope = new RequestScopeState(
            scopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var context = BuildSeparateTableResolverContext(
            plan,
            profileRequest: CreateRequest(scopeStates: requestScope)
        );
        var row = new FlattenedWriteValue[separatePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }

        _act = () =>
            new ProfileSeparateTableKeyUnificationResolver().Resolve(
                separatePlan,
                context,
                scopeAddress,
                requestScope,
                storedScope: null,
                row,
                ImmutableHashSet.Create(1)
            );
    }

    [Test]
    public void It_throws_ArgumentException_with_supported_kinds() =>
        _act.Should().Throw<ArgumentException>().WithMessage("*RootExtension*CollectionExtensionScope*Root*");
}

[TestFixture]
public class Given_SeparateTableResolver_instance_aware_overload_for_sibling_scope_instances
{
    private const string ParentScope = "$.parents[*]";
    private const string AlignedScope = "$.parents[*]._ext.aligned";

    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification(
            [
                new KeyUnificationMemberSpec(
                    RelativePath: "$.memberA",
                    SourceKind: KeyUnificationMemberSourceKind.Scalar,
                    PresenceSynthetic: false
                ),
            ],
            extensionJsonScope: AlignedScope
        );
        _canonicalIndex = canonicalIdx;

        var addressA = ScopeAddressForParent("A");
        var addressB = ScopeAddressForParent("B");
        var requestA = new RequestScopeState(addressA, ProfileVisibilityKind.VisiblePresent, true);
        var requestB = new RequestScopeState(addressB, ProfileVisibilityKind.VisiblePresent, true);
        var storedA = new StoredScopeState(addressA, ProfileVisibilityKind.VisiblePresent, ["memberA"]);
        var storedB = new StoredScopeState(addressB, ProfileVisibilityKind.VisiblePresent, []);

        var scopedBody = new JsonObject { ["memberA"] = 42 };
        var profileRequest = CreateRequest(
            writableBody: new JsonObject(),
            rootResourceCreatable: true,
            requestA,
            requestB
        );
        var appliedContext = CreateContext(profileRequest, visibleStoredBody: null, storedA, storedB);
        var currentRow = new Dictionary<DbColumnName, object?> { [MemberPathColumnFor("$.memberA")] = 99 };

        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: scopedBody,
            currentRowByColumnName: currentRow,
            profileRequest: profileRequest,
            profileAppliedContext: appliedContext
        );

        var separateTable = plan.TablePlansInDependencyOrder[1];
        _row = NewInitialRow(separateTable);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(
            separateTable,
            context,
            addressB,
            requestB,
            storedB,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_resolves_from_the_matching_instances_visible_request_member() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

    private static ScopeInstanceAddress ScopeAddressForParent(string parentId) =>
        new(
            AlignedScope,
            [
                new AncestorCollectionInstance(
                    ParentScope,
                    [new SemanticIdentityPart("$.parentId", JsonValue.Create(parentId), IsPresent: true)]
                ),
            ]
        );

    private static FlattenedWriteValue[] NewInitialRow(TableWritePlan tablePlan)
    {
        var row = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

/// <summary>
/// Slice 5 CP5 — descendant inlined scope governance for separate-table key-unification.
/// The k-u member's path falls under a descendant inlined non-collection scope
/// (<c>$._ext.sample.detail</c>) whose owner table equals the direct scope's table. The
/// descendant stored scope is <c>Hidden</c>; the request body provides a value that would
/// otherwise overwrite the stored value. The resolver, when given a descendant-state
/// envelope, must classify the member as hidden-governed under the descendant scope and
/// pull the canonical value from the stored row instead of the request body.
/// </summary>
[TestFixture]
public class Given_SeparateTableKuResolver_descendant_hidden_scope_preserves_stored_value
{
    private const string DirectScope = "$._ext.sample";
    private const string DescendantScope = "$._ext.sample.detail";
    private const string MemberRelativePath = "$._ext.sample.detail.memberA";

    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;
    private ProfileSeparateScopeDescendantStates _descendantStates;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceByPath) = BuildRootPlusRootExtensionPlanWithKeyUnification(
            [
                new KeyUnificationMemberSpec(
                    RelativePath: MemberRelativePath,
                    SourceKind: KeyUnificationMemberSourceKind.Scalar,
                    PresenceSynthetic: true
                ),
            ],
            extensionJsonScope: DirectScope
        );
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceByPath[MemberRelativePath];

        var directScopeAddress = new ScopeInstanceAddress(DirectScope, []);
        var descendantScopeAddress = new ScopeInstanceAddress(DescendantScope, []);

        // Direct scope is VisiblePresent. Descendant scope is VisiblePresent in the request
        // (the request body contains the descendant member with a competing value), but the
        // stored side declares the descendant scope Hidden — its governance must take effect
        // for bindings whose path falls under it.
        var directRequest = new RequestScopeState(
            directScopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var descendantRequest = new RequestScopeState(
            descendantScopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );
        var directStored = new StoredScopeState(
            directScopeAddress,
            ProfileVisibilityKind.VisiblePresent,
            ImmutableArray<string>.Empty
        );
        var descendantStored = new StoredScopeState(
            descendantScopeAddress,
            ProfileVisibilityKind.Hidden,
            ImmutableArray<string>.Empty
        );

        // Request body provides a value at the descendant member path (would overwrite the
        // stored value if the descendant scope's hidden governance were not honored).
        var requestBody = new JsonObject
        {
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject { ["detail"] = new JsonObject { ["memberA"] = 1 } },
            },
        };
        var profileRequest = CreateRequest(
            writableBody: requestBody,
            rootResourceCreatable: true,
            directRequest,
            descendantRequest
        );
        var appliedContext = CreateContext(
            profileRequest,
            visibleStoredBody: null,
            directStored,
            descendantStored
        );

        // Stored row contains the preserved value (99) for the hidden member; presence column
        // is non-null to signal the stored member is present.
        var memberColumn = MemberPathColumnFor(MemberRelativePath);
        var presenceColumn = PresenceColumnFor(MemberRelativePath);
        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [memberColumn] = 99,
            [presenceColumn] = true,
        };

        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: requestBody,
            currentRowByColumnName: currentRow,
            profileRequest: profileRequest,
            profileAppliedContext: appliedContext
        );

        var extensionTable = plan.TablePlansInDependencyOrder[1];
        _descendantStates = ProfileSeparateScopeDescendantStates.Collect(
            plan,
            extensionTable,
            directScopeAddress,
            profileRequest,
            appliedContext
        );

        _row = NewInitialRow(extensionTable);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx, _presenceIndex);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(
            extensionTable,
            context,
            directScopeAddress,
            directRequest,
            directStored,
            _descendantStates,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_pins_descendant_states_collector_finds_the_descendant_stored_scope() =>
        _descendantStates
            .StoredScopes.Should()
            .Contain(s =>
                s.Address.JsonScope == DescendantScope && s.Visibility == ProfileVisibilityKind.Hidden
            );

    [Test]
    public void It_writes_canonical_from_stored_hidden_descendant_value() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(99);

    [Test]
    public void It_writes_synthetic_presence_true_for_stored_present_descendant_member() =>
        ((FlattenedWriteValue.Literal)_row[_presenceIndex]).Value.Should().Be(true);

    private static FlattenedWriteValue[] NewInitialRow(TableWritePlan tablePlan)
    {
        var row = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

/// <summary>
/// Regression for the Slice 3 scope-navigation contract: when two key-unification
/// members are scope-relative (<c>$.memberA</c> and <c>$.memberB</c>) and the request
/// provides a visible value that disagrees with the stored value governed as hidden
/// under the same scope, the resolver must fail closed with a request-shape validation
/// exception. Pins that scope-relative member paths interact correctly with
/// per-scope <c>HiddenMemberPaths</c> governance during key-unification.
/// </summary>
