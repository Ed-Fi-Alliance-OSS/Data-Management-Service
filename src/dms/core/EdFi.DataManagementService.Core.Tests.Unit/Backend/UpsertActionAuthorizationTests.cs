// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
[Parallelizable]
public class UpsertActionAuthorizationTests
{
    private static AuthorizationStrategyEvaluator Evaluator(
        string name,
        params AuthorizationFilter[] filters
    ) => new(name, filters, FilterOperator.Or);

    private static readonly AuthorizationStrategyEvaluator _namespaceBased = Evaluator(
        AuthorizationStrategyNameConstants.NamespaceBased
    );

    private static readonly AuthorizationStrategyEvaluator _ownershipBased = Evaluator(
        AuthorizationStrategyNameConstants.OwnershipBased
    );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Permitted_Policy : UpsertActionAuthorizationTests
    {
        [Test]
        public void It_rejects_an_empty_evaluator_list()
        {
            var act = () => new UpsertActionPolicy.Permitted([]);

            act.Should().Throw<ArgumentException>().WithParameterName("evaluators");
        }

        [Test]
        public void It_rejects_a_null_evaluator_entry()
        {
            var act = () => new UpsertActionPolicy.Permitted([_namespaceBased, null!]);

            act.Should().Throw<ArgumentException>().WithParameterName("evaluators");
        }

        [Test]
        public void It_is_not_changed_by_later_edits_to_the_source_array()
        {
            AuthorizationStrategyEvaluator[] source = [_namespaceBased];
            var policy = new UpsertActionPolicy.Permitted(source);

            source[0] = _ownershipBased;

            policy.Evaluators.Should().Equal(_namespaceBased);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Action_Authorization : UpsertActionAuthorizationTests
    {
        [Test]
        public void It_rejects_a_null_create_policy()
        {
            var act = () => new UpsertActionAuthorization(null!, UpsertActionPolicy.NotPermitted.Instance);

            act.Should().Throw<ArgumentNullException>().WithParameterName("create");
        }

        [Test]
        public void It_rejects_a_null_update_policy()
        {
            var act = () => new UpsertActionAuthorization(UpsertActionPolicy.NotPermitted.Instance, null!);

            act.Should().Throw<ArgumentNullException>().WithParameterName("update");
        }

        [Test]
        public void It_returns_each_actions_own_policy()
        {
            var create = new UpsertActionPolicy.Permitted([_namespaceBased]);
            var update = UpsertActionPolicy.NotPermitted.Instance;
            var authorization = new UpsertActionAuthorization(create, update);

            authorization.For(UpsertTargetAction.Create).Should().BeSameAs(create);
            authorization.For(UpsertTargetAction.Update).Should().BeSameAs(update);
        }

        [Test]
        public void It_permits_both_actions_with_the_same_evaluators_when_asked_to()
        {
            var authorization = UpsertActionAuthorization.SamePolicyForCreateAndUpdate([_namespaceBased]);

            authorization
                .Create.Should()
                .BeOfType<UpsertActionPolicy.Permitted>()
                .Which.Evaluators.Should()
                .Equal(_namespaceBased);
            authorization.Update.Should().BeSameAs(authorization.Create);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Shared_Policy_Detection : UpsertActionAuthorizationTests
    {
        private static UpsertActionAuthorization Pair(
            AuthorizationStrategyEvaluator[] create,
            AuthorizationStrategyEvaluator[] update
        ) => new(new UpsertActionPolicy.Permitted(create), new UpsertActionPolicy.Permitted(update));

        [Test]
        public void It_shares_equivalent_evaluators_built_separately()
        {
            var authorization = Pair(
                [_namespaceBased, Evaluator(AuthorizationStrategyNameConstants.OwnershipBased)],
                [
                    Evaluator(AuthorizationStrategyNameConstants.NamespaceBased),
                    Evaluator(AuthorizationStrategyNameConstants.OwnershipBased),
                ]
            );

            authorization.TryGetSharedPolicy(out var evaluators).Should().BeTrue();
            evaluators.Should().Equal(_namespaceBased, _ownershipBased);
        }

        [Test]
        public void It_does_not_share_the_same_names_in_a_different_order()
        {
            var authorization = Pair([_namespaceBased, _ownershipBased], [_ownershipBased, _namespaceBased]);

            authorization.TryGetSharedPolicy(out _).Should().BeFalse();
        }

        [Test]
        public void It_does_not_share_a_duplicated_strategy()
        {
            var authorization = Pair([_namespaceBased, _namespaceBased], [_namespaceBased]);

            authorization.TryGetSharedPolicy(out _).Should().BeFalse();
        }

        [Test]
        public void It_does_not_share_names_that_differ_only_by_case()
        {
            var authorization = Pair([_namespaceBased], [Evaluator("namespacebased")]);

            authorization.TryGetSharedPolicy(out _).Should().BeFalse();
        }

        [Test]
        public void It_does_not_share_evaluators_with_different_filters()
        {
            var authorization = Pair(
                [Evaluator("Custom", new AuthorizationFilter.EducationOrganization("255901"))],
                [Evaluator("Custom", new AuthorizationFilter.EducationOrganization("255902"))]
            );

            authorization.TryGetSharedPolicy(out _).Should().BeFalse();
        }

        [Test]
        public void It_does_not_share_evaluators_with_different_operators()
        {
            var authorization = Pair(
                [_namespaceBased],
                [_namespaceBased with { Operator = FilterOperator.And }]
            );

            authorization.TryGetSharedPolicy(out _).Should().BeFalse();
        }

        [Test]
        public void It_does_not_share_when_an_action_is_not_permitted()
        {
            var authorization = new UpsertActionAuthorization(
                new UpsertActionPolicy.Permitted([_namespaceBased]),
                UpsertActionPolicy.NotPermitted.Instance
            );

            authorization.TryGetSharedPolicy(out var evaluators).Should().BeFalse();
            evaluators.Should().BeEmpty();
        }
    }
}
