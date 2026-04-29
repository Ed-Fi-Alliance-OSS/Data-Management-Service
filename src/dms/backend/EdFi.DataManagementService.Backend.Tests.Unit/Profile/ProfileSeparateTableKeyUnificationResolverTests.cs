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
public class Given_SeparateTableResolver_with_no_key_unification_plans_does_nothing
{
    private FlattenedWriteValue[] _row = null!;
    private FlattenedWriteValue[] _rowBefore = null!;

    [SetUp]
    public void Setup()
    {
        // RootExtension table with no key-unification plans — resolver must short-circuit.
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var extensionPlan = plan.TablePlansInDependencyOrder[1];

        var context = BuildSeparateTableResolverContext(
            plan,
            profileRequest: CreateRequest(
                writableBody: null,
                rootResourceCreatable: true,
                RequestVisiblePresentScope("$"),
                RequestVisiblePresentScope("$._ext.sample")
            )
        );

        _row = new FlattenedWriteValue[extensionPlan.ColumnBindings.Length];
        for (var i = 0; i < _row.Length; i++)
        {
            _row[i] = new FlattenedWriteValue.Literal($"SEED_{i}");
        }
        _rowBefore = (FlattenedWriteValue[])_row.Clone();

        new ProfileSeparateTableKeyUnificationResolver().Resolve(
            extensionPlan,
            context,
            _row,
            ImmutableHashSet<int>.Empty
        );
    }

    [Test]
    public void It_preserves_every_binding_value()
    {
        for (var i = 0; i < _row.Length; i++)
        {
            ((FlattenedWriteValue.Literal)_row[i])
                .Value.Should()
                .Be(((FlattenedWriteValue.Literal)_rowBefore[i]).Value);
        }
    }
}

[TestFixture]
public class Given_SeparateTableResolver_with_visible_members_computes_canonical_from_request
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);
        _canonicalIndex = canonicalIdx;

        // Request body must shape the extension scope so the visible member resolves.
        // _ext.sample.memberA path in the body.
        var body = new JsonObject
        {
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 42 } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );

        var context = BuildSeparateTableResolverContext(plan, writableBody: body, profileRequest: request);

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        _row = NewInitialRow(extensionPlan);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(extensionPlan, context, _row, resolverOwned);
    }

    [Test]
    public void It_writes_canonical_value_from_request_body() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

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

[TestFixture]
public class Given_SeparateTableResolver_with_hidden_governed_member_preserves_stored_value
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIndicesByPath["$._ext.sample.memberA"];

        // Stored scope at $._ext.sample hides "memberA" — resolver pulls value from stored row.
        var request = CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$._ext.sample", "memberA")
        );

        var memberColumn = MemberPathColumnFor("$._ext.sample.memberA");
        var presenceColumn = PresenceColumnFor("$._ext.sample.memberA");
        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [memberColumn] = 99,
            [presenceColumn] = true,
        };

        var context = BuildSeparateTableResolverContext(
            plan,
            currentRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        _row = NewInitialRow(extensionPlan);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx, _presenceIndex);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(extensionPlan, context, _row, resolverOwned);
    }

    [Test]
    public void It_writes_canonical_from_stored_hidden_value() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(99);

    [Test]
    public void It_writes_synthetic_presence_true_for_stored_present() =>
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

[TestFixture]
public class Given_SeparateTableResolver_with_mixed_visible_and_hidden_agreement_succeeds
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberB",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);
        _canonicalIndex = canonicalIdx;

        // Visible memberA from request (42); hidden memberB from stored row (42) — agree.
        var body = new JsonObject
        {
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 42 } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$._ext.sample", "memberB")
        );

        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [MemberPathColumnFor("$._ext.sample.memberA")] = null,
            [MemberPathColumnFor("$._ext.sample.memberB")] = 42,
        };

        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: body,
            currentRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        _row = NewInitialRow(extensionPlan);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(extensionPlan, context, _row, resolverOwned);
    }

    [Test]
    public void It_writes_canonical_from_visible_or_hidden_consistently() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

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

[TestFixture]
public class Given_SeparateTableResolver_with_visible_hidden_disagreement_fails_closed
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberB",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);

        // Visible memberA = 1, hidden memberB (stored) = 2 — disagree.
        var body = new JsonObject
        {
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 1 } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$._ext.sample", "memberB")
        );

        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [MemberPathColumnFor("$._ext.sample.memberA")] = null,
            [MemberPathColumnFor("$._ext.sample.memberB")] = 2,
        };

        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: body,
            currentRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var row = new FlattenedWriteValue[extensionPlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        _act = () =>
            new ProfileSeparateTableKeyUnificationResolver().Resolve(
                extensionPlan,
                context,
                row,
                resolverOwned
            );
    }

    [Test]
    public void It_throws_validation_exception_with_key_unification_conflict_message()
    {
        _act.Should()
            .Throw<RelationalWriteRequestValidationException>()
            .Where(e => e.ValidationFailures[0].Message.Contains("Key-unification conflict"));
    }
}

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
public class Given_SeparateTableResolver_legacy_overload_for_CollectionExtensionScope_table_kind
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusSeparateTablePlanWithNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.CollectionExtensionScope
        );
        var separatePlan = plan.TablePlansInDependencyOrder[1];
        var context = BuildSeparateTableResolverContext(
            plan,
            profileRequest: CreateRequest(scopeStates: RequestVisiblePresentScope("$._ext.sample"))
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
                row,
                ImmutableHashSet<int>.Empty
            );
    }

    [Test]
    public void It_throws_ArgumentException_to_preserve_instance_aware_scope_lookup() =>
        _act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*legacy*RootExtension*CollectionExtensionScope*");
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
public class Given_SeparateTableResolver_with_stored_context_containing_unrelated_root_scope_resolves_without_contamination
{
    // Multi-table regression pin: the profile context may carry stored scope states for
    // scopes owned by other tables (e.g., the root $ when resolving an extension table's
    // key-unification plan). The resolver's core uses longest-prefix scope matching on the
    // member's own canonical path, so it should select the extension scope owner and
    // ignore the root scope even when the root also declares HiddenMemberPaths of its own.
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$._ext.sample.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);
        _canonicalIndex = canonicalIdx;

        // Request body supplies the extension member; both root and extension scopes present.
        var body = new JsonObject
        {
            ["firstName"] = "Alice",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 42 } },
        };
        var request = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );

        // Stored side: BOTH scopes present. The root scope ($) declares HiddenMemberPaths
        // for the leaf name "memberA" — the identical leaf identifier the extension scope
        // uses at $._ext.sample.memberA. If scope ownership were determined by leaf name
        // (instead of by longest table-backed scope prefix), the root's hidden-path entry
        // would incorrectly bind to the extension member and cause the resolver to pull
        // the (null) stored value for the extension member column instead of reading from
        // the request body.
        var appliedContext = CreateContext(
            request,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$", "memberA"),
            StoredVisiblePresentScope("$._ext.sample")
        );

        // If contamination happened, the resolver would look up the extension's member
        // column in the stored row and find null — but we leave it absent so we'd get a
        // missing-column InvalidOperationException. Under correct table-localized scope
        // matching, the resolver reads from the request body and writes 42.
        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: body,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        _row = NewInitialRow(extensionPlan);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(extensionPlan, context, _row, resolverOwned);
    }

    [Test]
    public void It_writes_canonical_from_request_body_ignoring_unrelated_root_scope() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

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
/// Regression for the Slice 3 scope-navigation contract: production key-unification
/// member paths on a root-extension plan are compiled as scope-relative
/// (<c>$.memberA</c>, not <c>$._ext.sample.memberA</c>). After the synthesizer fix,
/// the caller hands the scope-scoped sub-node of the root request body to the
/// resolver; the resolver then finds the member by navigating the scope-relative
/// path against that scoped node.
/// </summary>
[TestFixture]
public class Given_SeparateTableResolver_with_scope_relative_member_path_evaluates_against_scoped_request_node
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    [SetUp]
    public void Setup()
    {
        // Production-shaped: member path is scope-relative; the scope is $._ext.sample.
        var (plan, canonicalIdx, presenceByPath) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceByPath["$.memberA"];

        // Full root request body contains _ext.sample.memberA — matches production shape.
        // The fixed caller extracts the $._ext.sample sub-node and passes THAT to the
        // resolver context, so scope-relative member paths navigate correctly.
        var rootBody = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 42 } },
        };
        var scopedRequestNode = rootBody["_ext"]!["sample"]!;

        var request = CreateRequest(
            writableBody: rootBody,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );

        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: scopedRequestNode,
            profileRequest: request
        );

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        _row = NewInitialRow(extensionPlan);
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx, _presenceIndex);

        new ProfileSeparateTableKeyUnificationResolver().Resolve(extensionPlan, context, _row, resolverOwned);
    }

    [Test]
    public void It_writes_canonical_from_scope_relative_member_path() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

    [Test]
    public void It_writes_synthetic_presence_true_for_visible_present_member() =>
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
/// Regression for the Slice 3 scope-navigation contract: when two key-unification
/// members are scope-relative (<c>$.memberA</c> and <c>$.memberB</c>) and the request
/// provides a visible value that disagrees with the stored value governed as hidden
/// under the same scope, the resolver must fail closed with a request-shape validation
/// exception. Pins that scope-relative member paths interact correctly with
/// per-scope <c>HiddenMemberPaths</c> governance during key-unification.
/// </summary>
[TestFixture]
public class Given_SeparateTableResolver_with_scope_relative_visible_hidden_disagreement_fails_closed
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlusRootExtensionPlanWithKeyUnification([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberB",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);

        // Visible memberA = 1 (from scoped request); hidden memberB (stored) = 2 — disagree.
        var rootBody = new JsonObject
        {
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["memberA"] = 1 } },
        };
        var scopedRequestNode = rootBody["_ext"]!["sample"]!;
        var request = CreateRequest(
            writableBody: rootBody,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            RequestVisiblePresentScope("$._ext.sample")
        );
        // Hidden-member path on the stored scope is scope-relative ("memberB"), matching
        // the way the classifier derives the member's governing path from its scope.
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$._ext.sample", "memberB")
        );

        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [MemberPathColumnFor("$.memberA")] = null,
            [MemberPathColumnFor("$.memberB")] = 2,
        };

        var context = BuildSeparateTableResolverContext(
            plan,
            writableBody: scopedRequestNode,
            currentRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );

        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var row = new FlattenedWriteValue[extensionPlan.ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        _act = () =>
            new ProfileSeparateTableKeyUnificationResolver().Resolve(
                extensionPlan,
                context,
                row,
                resolverOwned
            );
    }

    [Test]
    public void It_throws_validation_exception_with_key_unification_conflict_message()
    {
        _act.Should()
            .Throw<RelationalWriteRequestValidationException>()
            .Where(e => e.ValidationFailures[0].Message.Contains("Key-unification conflict"));
    }
}
