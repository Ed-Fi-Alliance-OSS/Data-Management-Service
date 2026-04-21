// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
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
public class Given_SeparateTableResolver_rejects_non_RootExtension_table_kind
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // Separate table tagged as CollectionExtensionScope — the slice 5 scope family
        // the separate-table resolver must reject.
        var plan = BuildRootPlusSeparateTableWithKeyUnificationNonRootExtensionKind(
            nonRootExtensionKind: DbTableKind.CollectionExtensionScope
        );
        var separatePlan = plan.TablePlansInDependencyOrder[1];

        var context = BuildSeparateTableResolverContext(
            plan,
            profileRequest: CreateRequest(scopeStates: RequestVisiblePresentScope("$"))
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
                ImmutableHashSet.Create(1)
            );
    }

    [Test]
    public void It_throws_ArgumentException_with_slice_5_guidance() =>
        _act.Should().Throw<ArgumentException>().WithMessage("*RootExtension*slice 5*");
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
