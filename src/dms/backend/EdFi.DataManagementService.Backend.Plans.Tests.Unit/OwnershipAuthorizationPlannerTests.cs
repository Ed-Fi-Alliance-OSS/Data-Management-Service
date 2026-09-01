// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_OwnershipAuthorizationPlanner
{
    private static ConfiguredAuthorizationStrategy Ownership(int rawConfiguredIndex) =>
        new(AuthorizationStrategyNameConstants.OwnershipBased, rawConfiguredIndex);

    /// <summary>
    /// One check for every single-record operation. <c>Update</c> covers both write verbs, because a POST
    /// resolves to a create or an upsert-as-update only in-session.
    /// </summary>
    [TestCase(NamespaceAuthorizationOperation.ReadSingle)]
    [TestCase(NamespaceAuthorizationOperation.Update)]
    [TestCase(NamespaceAuthorizationOperation.Delete)]
    public void It_plans_one_stored_check_for_each_single_record_operation(
        NamespaceAuthorizationOperation operation
    )
    {
        var check = OwnershipAuthorizationPlanner.Plan(operation, [Ownership(0)]);

        check.RawConfiguredIndex.Should().Be(0);
        check.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// The configured position travels into the AUTH1 payload, so it must be the real one rather than a
    /// normalized zero.
    /// </summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    public void It_carries_the_configured_position(int rawConfiguredIndex)
    {
        OwnershipAuthorizationPlanner
            .Plan(NamespaceAuthorizationOperation.ReadSingle, [Ownership(rawConfiguredIndex)])
            .RawConfiguredIndex.Should()
            .Be(rawConfiguredIndex);
    }

    /// <summary>
    /// Configuring <c>OwnershipBased</c> twice collapses to one check at the earliest occurrence. The check
    /// reads one column against one token list, so it evaluates once however many times it is configured, and
    /// the position where it first executes is the earliest. Stamping a later one would let a custom view
    /// configured between them validate ahead of a terminal it actually follows.
    /// </summary>
    [Test]
    public void It_collapses_repeated_configuration_to_the_earliest_occurrence()
    {
        OwnershipAuthorizationPlanner
            .Plan(NamespaceAuthorizationOperation.Update, [Ownership(3), Ownership(1)])
            .RawConfiguredIndex.Should()
            .Be(1);
    }

    /// <summary>
    /// The result must not depend on the caller handing the list in configured order.
    /// </summary>
    [Test]
    public void It_does_not_depend_on_the_order_the_strategies_arrive_in()
    {
        var ascending = OwnershipAuthorizationPlanner.Plan(
            NamespaceAuthorizationOperation.Delete,
            [Ownership(2), Ownership(5)]
        );
        var descending = OwnershipAuthorizationPlanner.Plan(
            NamespaceAuthorizationOperation.Delete,
            [Ownership(5), Ownership(2)]
        );

        ascending.Should().Be(descending);
    }

    /// <summary>
    /// GET-many ownership filtering is a different shape of check owned by DMS-1410, so this single-record
    /// planner must refuse it rather than serve a plan that would filter nothing.
    /// </summary>
    [Test]
    public void It_refuses_read_many()
    {
        Action act = () =>
            OwnershipAuthorizationPlanner.Plan(NamespaceAuthorizationOperation.ReadMany, [Ownership(0)]);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_refuses_an_unrecognized_operation()
    {
        Action act = () =>
            OwnershipAuthorizationPlanner.Plan((NamespaceAuthorizationOperation)999, [Ownership(0)]);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_refuses_an_empty_strategy_list()
    {
        Action act = () => OwnershipAuthorizationPlanner.Plan(NamespaceAuthorizationOperation.ReadSingle, []);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_refuses_a_null_strategy_list()
    {
        Action act = () =>
            OwnershipAuthorizationPlanner.Plan(NamespaceAuthorizationOperation.ReadSingle, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// A non-ownership strategy arriving here means the caller's bucket split is wrong, which would
    /// authorize the wrong strategy's subject. It must fail loudly rather than be planned as ownership.
    /// </summary>
    [TestCase(AuthorizationStrategyNameConstants.NamespaceBased)]
    [TestCase(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly)]
    [TestCase("StudentWithCTECourseEnrollments")]
    public void It_refuses_a_strategy_that_is_not_ownership_based(string strategyName)
    {
        Action act = () =>
            OwnershipAuthorizationPlanner.Plan(
                NamespaceAuthorizationOperation.ReadSingle,
                [Ownership(0), new ConfiguredAuthorizationStrategy(strategyName, 1)]
            );

        act.Should().Throw<ArgumentException>().WithMessage($"*{strategyName}*");
    }
}
