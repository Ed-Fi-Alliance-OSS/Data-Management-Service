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
public class Given_Resolver_with_single_visible_present_scalar_member
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIndicesByPath["$.memberA"];

        var body = new JsonObject { ["memberA"] = 42 };
        var context = BuildResolverContext(
            plan,
            writableBody: body,
            profileRequest: CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"))
        );

        _row = BuildInitialRow(plan);
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex, _presenceIndex);
        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_value_from_request_body() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

    [Test]
    public void It_writes_synthetic_presence_true() =>
        ((FlattenedWriteValue.Literal)_row[_presenceIndex]).Value.Should().Be(true);

    private static FlattenedWriteValue[] BuildInitialRow(ResourceWritePlan plan)
    {
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

[TestFixture]
public class Given_Resolver_with_single_visible_absent_member
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers(
            [
                new KeyUnificationMemberSpec(
                    RelativePath: "$.memberA",
                    SourceKind: KeyUnificationMemberSourceKind.Scalar,
                    PresenceSynthetic: true
                ),
            ],
            canonicalIsNullable: true
        );
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIndicesByPath["$.memberA"];

        var context = BuildResolverContext(
            plan,
            profileRequest: CreateRequest(scopeStates: RequestVisibleAbsentScope("$"))
        );
        _row = NewInitialRow(plan);
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex, _presenceIndex);

        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_null() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().BeNull();

    [Test]
    public void It_writes_synthetic_presence_null() =>
        ((FlattenedWriteValue.Literal)_row[_presenceIndex]).Value.Should().BeNull();

    private static FlattenedWriteValue[] NewInitialRow(ResourceWritePlan plan)
    {
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

[TestFixture]
public class Given_Resolver_with_single_hidden_stored_present_member
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;
    private int _presenceIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        _canonicalIndex = canonicalIdx;
        _presenceIndex = presenceIndicesByPath["$.memberA"];

        var request = CreateRequest(scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "memberA")
        );

        var memberColumn = MemberPathColumnFor("$.memberA");
        var presenceColumn = PresenceColumnFor("$.memberA");
        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [memberColumn] = 99,
            [presenceColumn] = true,
        };

        var context = BuildResolverContext(
            plan,
            currentRootRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );
        _row = NewInitialRow(plan);
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex, _presenceIndex);

        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_from_stored_hidden_value() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(99);

    [Test]
    public void It_writes_synthetic_presence_true_for_stored_present() =>
        ((FlattenedWriteValue.Literal)_row[_presenceIndex]).Value.Should().Be(true);

    private static FlattenedWriteValue[] NewInitialRow(ResourceWritePlan plan)
    {
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

[TestFixture]
public class Given_Resolver_with_visible_and_hidden_members_agreeing
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlanWithKeyUnificationMembers([
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
        _canonicalIndex = canonicalIdx;

        var body = new JsonObject { ["memberA"] = 42 };
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "memberB")
        );

        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [MemberPathColumnFor("$.memberA")] = null,
            [MemberPathColumnFor("$.memberB")] = 42,
        };

        var context = BuildResolverContext(
            plan,
            writableBody: body,
            currentRootRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );
        _row = NewInitialRow(plan);
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex);

        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_from_visible_or_hidden_consistently() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(42);

    private static FlattenedWriteValue[] NewInitialRow(ResourceWritePlan plan)
    {
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}

[TestFixture]
public class Given_Resolver_with_visible_and_hidden_members_disagreeing
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlanWithKeyUnificationMembers([
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

        var body = new JsonObject { ["memberA"] = 1 };
        var request = CreateRequest(writableBody: body, scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "memberB")
        );

        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [MemberPathColumnFor("$.memberA")] = null,
            [MemberPathColumnFor("$.memberB")] = 2,
        };

        var context = BuildResolverContext(
            plan,
            writableBody: body,
            currentRootRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        _act = () =>
            new ProfileRootKeyUnificationResolver().Resolve(
                plan.TablePlansInDependencyOrder[0],
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
public class Given_Resolver_with_presence_non_null_and_canonical_null_guardrail
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        var presenceIdx = presenceIndicesByPath["$.memberA"];

        // Stored scope hides memberA; stored presence column is non-null (so the resolver
        // treats the member as present) while the stored value is null. Resolver writes
        // presence=true and canonical=null; the presence-gated-null guardrail must fire.
        var request = CreateRequest(scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "memberA")
        );

        var currentRow = new Dictionary<DbColumnName, object?>
        {
            [MemberPathColumnFor("$.memberA")] = null,
            [PresenceColumnFor("$.memberA")] = true,
        };

        var context = BuildResolverContext(
            plan,
            currentRootRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx, presenceIdx);

        _act = () =>
            new ProfileRootKeyUnificationResolver().Resolve(
                plan.TablePlansInDependencyOrder[0],
                context,
                row,
                resolverOwned
            );
    }

    [Test]
    public void It_throws_InvalidOperationException_for_presence_gated_null()
    {
        _act.Should().Throw<InvalidOperationException>().WithMessage("*resolved to null while presence*");
    }
}

[TestFixture]
public class Given_Resolver_with_canonical_non_nullable_and_no_member_present_guardrail
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlanWithKeyUnificationMembers(
            [
                new KeyUnificationMemberSpec(
                    RelativePath: "$.memberA",
                    SourceKind: KeyUnificationMemberSourceKind.Scalar,
                    PresenceSynthetic: false
                ),
            ],
            canonicalIsNullable: false
        );

        // Visible-absent, no hidden — canonical ends up null, and canonical column is
        // non-nullable → guardrail throws.
        var context = BuildResolverContext(
            plan,
            profileRequest: CreateRequest(scopeStates: RequestVisibleAbsentScope("$"))
        );
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        _act = () =>
            new ProfileRootKeyUnificationResolver().Resolve(
                plan.TablePlansInDependencyOrder[0],
                context,
                row,
                resolverOwned
            );
    }

    [Test]
    public void It_throws_InvalidOperationException_for_non_nullable_canonical_null()
    {
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*is not nullable but resolved to null*");
    }
}

[TestFixture]
public class Given_Resolver_for_CreateNew_with_no_stored_state_collapses_to_flattener_result
{
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: false
            ),
        ]);
        _canonicalIndex = canonicalIdx;

        var body = new JsonObject { ["memberA"] = 7 };
        // No profile request scope states → no governance → behaves like flattener.
        var context = BuildResolverContext(plan, writableBody: body);

        _row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < _row.Length; i++)
        {
            _row[i] = new FlattenedWriteValue.Literal(null);
        }
        var resolverOwned = ImmutableHashSet.Create(canonicalIdx);

        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_matching_flattener_evaluation() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(7);
}

[TestFixture]
public class Given_Resolver_writes_every_canonical_and_synthetic_presence_binding_required
{
    private FlattenedWriteValue[] _row = null!;
    private ImmutableHashSet<int> _resolverOwned = null!;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberB",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        var presenceAIdx = presenceIndicesByPath["$.memberA"];
        var presenceBIdx = presenceIndicesByPath["$.memberB"];

        var body = new JsonObject { ["memberA"] = 11 };
        var context = BuildResolverContext(plan, writableBody: body);

        _row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < _row.Length; i++)
        {
            _row[i] = new FlattenedWriteValue.Literal(null);
        }
        _resolverOwned = ImmutableHashSet.Create(canonicalIdx, presenceAIdx, presenceBIdx);

        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            _resolverOwned
        );
    }

    [Test]
    public void It_assigns_a_literal_to_every_resolver_owned_index()
    {
        foreach (var idx in _resolverOwned)
        {
            _row[idx].Should().BeOfType<FlattenedWriteValue.Literal>();
        }
    }
}

[TestFixture]
public class Given_Resolver_does_not_write_outside_resolver_owned_indices
{
    private FlattenedWriteValue[] _row = null!;
    private int _nonResolverOwnedIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, presenceIndicesByPath) = BuildRootPlanWithKeyUnificationMembers([
            new KeyUnificationMemberSpec(
                RelativePath: "$.memberA",
                SourceKind: KeyUnificationMemberSourceKind.Scalar,
                PresenceSynthetic: true
            ),
        ]);
        var presenceIdx = presenceIndicesByPath["$.memberA"];

        // Locate a binding outside canonical and synthetic-presence indices so the
        // resolver's invariant of not touching non-resolver-owned indices can be verified.
        var bindings = plan.TablePlansInDependencyOrder[0].ColumnBindings;
        for (var i = 0; i < bindings.Length; i++)
        {
            if (i != canonicalIdx && i != presenceIdx)
            {
                _nonResolverOwnedIndex = i;
                break;
            }
        }

        var body = new JsonObject { ["memberA"] = 1 };
        var context = BuildResolverContext(plan, writableBody: body);

        _row = new FlattenedWriteValue[bindings.Length];
        for (var i = 0; i < _row.Length; i++)
        {
            _row[i] = new FlattenedWriteValue.Literal(null);
        }
        // Preseed the non-resolver-owned binding with a sentinel value.
        _row[_nonResolverOwnedIndex] = new FlattenedWriteValue.Literal("SENTINEL");

        var resolverOwned = ImmutableHashSet.Create(canonicalIdx, presenceIdx);
        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_preserves_pre_existing_value_at_non_resolver_owned_index() =>
        ((FlattenedWriteValue.Literal)_row[_nonResolverOwnedIndex]).Value.Should().Be("SENTINEL");
}

[TestFixture]
public class Given_Resolver_with_reference_derived_member_and_hidden_sibling_sub_reference_path
{
    // Hidden-reference-governance regression (profiles.md:782): hiding a sibling identity path
    // of the same reference (schoolReference.schoolId) must still preserve THIS reference-derived
    // member (schoolReference.localEducationAgencyId) because both derive from the same owning
    // reference root (schoolReference). Under the pre-Phase-2 rule this member would not have
    // been governed by the hidden sibling path and would have picked up a visible-absent value.
    private FlattenedWriteValue[] _row = null!;
    private int _canonicalIndex;

    [SetUp]
    public void Setup()
    {
        var (plan, canonicalIdx, _, memberColumnName) = BuildRootPlanWithReferenceDerivedKeyUnificationMember(
            referenceMemberPath: "$.schoolReference",
            derivedMemberPath: "$.schoolReference.localEducationAgencyId"
        );
        _canonicalIndex = canonicalIdx;

        var request = CreateRequest(scopeStates: RequestVisiblePresentScope("$"));
        var appliedContext = CreateContext(
            request,
            storedScopeStates: StoredVisiblePresentScope("$", "schoolReference.schoolId")
        );

        // Stored value that must be preserved by hidden-reference governance.
        var currentRow = new Dictionary<DbColumnName, object?> { [memberColumnName] = 777 };

        var context = BuildResolverContext(
            plan,
            currentRootRowByColumnName: currentRow,
            profileRequest: request,
            profileAppliedContext: appliedContext
        );

        _row = NewInitialRow(plan);
        var resolverOwned = ImmutableHashSet.Create(_canonicalIndex);
        new ProfileRootKeyUnificationResolver().Resolve(
            plan.TablePlansInDependencyOrder[0],
            context,
            _row,
            resolverOwned
        );
    }

    [Test]
    public void It_writes_canonical_from_stored_hidden_reference_derived_value() =>
        ((FlattenedWriteValue.Literal)_row[_canonicalIndex]).Value.Should().Be(777);

    private static FlattenedWriteValue[] NewInitialRow(ResourceWritePlan plan)
    {
        var row = new FlattenedWriteValue[plan.TablePlansInDependencyOrder[0].ColumnBindings.Length];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = new FlattenedWriteValue.Literal(null);
        }
        return row;
    }
}
