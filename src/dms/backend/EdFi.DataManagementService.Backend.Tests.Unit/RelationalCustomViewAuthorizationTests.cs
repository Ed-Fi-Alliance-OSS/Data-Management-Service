// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The value-source slices exist so execution never has to re-derive them, but they must never disagree with
/// the full planned list they came from — that list is the only authority for resolving a <c>cv1</c> payload.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_RelationalCustomViewAuthorization
{
    private static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "School");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    private static SingleRecordCustomViewAuthorizationCheckSpec Check(
        int index,
        CustomViewAuthorizationCheckValueSource valueSource,
        string strategyName
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(strategyName, 0),
            index,
            valueSource,
            new DbTableName(new DbSchemaName("auth"), strategyName),
            DocumentIdColumn,
            [new ColumnPathStep(RootTable, DocumentIdColumn, null, null)],
            valueSource is CustomViewAuthorizationCheckValueSource.Stored
                ? new CustomViewAuthorizationCheckTarget.Stored(RootTable, DocumentIdColumn)
                : new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(RootTable),
            new QualifiedResourceName("Ed-Fi", "School"),
            [$"{strategyName}Element"],
            $"You may need a {strategyName} hint."
        );

    /// <summary>An Update plan: stored checks first, then the proposed pair, indexed request-wide.</summary>
    private static RelationalCustomViewAuthorization CreateUpdatePlan() =>
        new([
            Check(0, CustomViewAuthorizationCheckValueSource.Stored, "SchoolWithATag"),
            Check(1, CustomViewAuthorizationCheckValueSource.Stored, "SchoolWithAnotherTag"),
            Check(2, CustomViewAuthorizationCheckValueSource.Proposed, "SchoolWithATag"),
            Check(3, CustomViewAuthorizationCheckValueSource.Proposed, "SchoolWithAnotherTag"),
        ]);

    [Test]
    public void It_keeps_the_full_planned_list_intact()
    {
        CreateUpdatePlan().Checks.Select(check => check.Index).Should().Equal(0, 1, 2, 3);
    }

    [Test]
    public void It_slices_by_value_source_preserving_request_wide_indexes()
    {
        var plan = CreateUpdatePlan();

        plan.StoredChecks.Select(check => check.Index).Should().Equal(0, 1);
        // The proposed slice starts above zero, which is what lets both slices co-batch without their
        // payload indexes colliding.
        plan.ProposedChecks.Select(check => check.Index).Should().Equal(2, 3);
    }

    [Test]
    public void It_slices_a_stored_only_plan_to_the_whole_list_and_nothing_proposed()
    {
        // GET-by-id and DELETE plan stored values only, so the stored slice is the full list.
        var plan = new RelationalCustomViewAuthorization([
            Check(0, CustomViewAuthorizationCheckValueSource.Stored, "SchoolWithATag"),
        ]);

        plan.StoredChecks.Should().Equal(plan.Checks);
        plan.ProposedChecks.Should().BeEmpty();
    }

    [Test]
    public void It_recomputes_both_slices_for_a_different_set_of_checks()
    {
        // Producing a different set requires a new instance — Checks is not init-settable, so a copy cannot
        // carry slices computed from some other list.
        var storedOnly = new RelationalCustomViewAuthorization([
            Check(0, CustomViewAuthorizationCheckValueSource.Stored, "SchoolWithATag"),
        ]);
        var withProposed = new RelationalCustomViewAuthorization([
            .. storedOnly.Checks,
            Check(1, CustomViewAuthorizationCheckValueSource.Proposed, "SchoolWithATag"),
        ]);

        storedOnly.ProposedChecks.Should().BeEmpty();
        withProposed.StoredChecks.Select(check => check.Index).Should().Equal(0);
        withProposed.ProposedChecks.Select(check => check.Index).Should().Equal(1);
    }

    [Test]
    public void It_is_unaffected_by_mutating_the_list_it_was_constructed_from()
    {
        // IReadOnlyList is a read-only view, not an immutable value, so the constructor copies. Without that
        // copy a caller could append after construction and leave the slices describing contents Checks no
        // longer reported — the same stale-slice divergence, reached from outside.
        List<SingleRecordCustomViewAuthorizationCheckSpec> mutableChecks =
        [
            Check(0, CustomViewAuthorizationCheckValueSource.Stored, "SchoolWithATag"),
        ];
        var plan = new RelationalCustomViewAuthorization(mutableChecks);

        mutableChecks.Add(Check(1, CustomViewAuthorizationCheckValueSource.Proposed, "SchoolWithATag"));
        mutableChecks[0] = Check(
            9,
            CustomViewAuthorizationCheckValueSource.Stored,
            "SchoolWithSomethingElse"
        );

        plan.Checks.Should().ContainSingle();
        plan.Checks[0].ConfiguredStrategy.StrategyName.Should().Be("SchoolWithATag");
        plan.Checks[0].Index.Should().Be(0);
        plan.StoredChecks.Should().Equal(plan.Checks);
        plan.ProposedChecks.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_a_missing_check_list()
    {
        var act = () => new RelationalCustomViewAuthorization(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
