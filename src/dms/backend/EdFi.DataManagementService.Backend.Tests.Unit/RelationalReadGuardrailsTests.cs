// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalReadGuardrails_SecurityConfigurationDiagnostics
{
    private static readonly QualifiedResourceName _section = new("Ed-Fi", "Section");

    private static RelationalReadSecurityConfigurationFailure BuildFailure(
        RelationshipAuthorizationFailureMetadata failure
    ) =>
        RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            _section,
            [failure.ConfiguredStrategy!],
            new RelationshipAuthorizationClassification(
                RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError,
                [],
                [],
                [],
                [],
                [failure]
            )
        );

    [TestCase(RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource)]
    [TestCase(RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable)]
    public void It_reports_the_target_resource_for_custom_view_basis_failures(
        RelationshipAuthorizationFailureKind failureKind
    )
    {
        var failure = BuildFailure(
            new RelationshipAuthorizationFailureMetadata(
                failureKind,
                _section,
                new ConfiguredAuthorizationStrategy("LocationWithX", 2),
                RelationshipLocalOrder: 2,
                Location: new RelationshipAuthorizationFailureLocation(
                    AuthorizationObjectName: "auth.LocationWithX"
                ),
                Hint: "hint"
            )
        );

        var diagnostic = failure.Diagnostics.Should().ContainSingle().Subject;
        diagnostic.ProviderOrPlannerFailureKind.Should().Be($"RelationshipAuthorization.{failureKind}");
        diagnostic.ResourceFullName.Should().Be("Ed-Fi.Section");
        diagnostic.TargetResourceFullName.Should().Be("Ed-Fi.Section");
        diagnostic.ConfiguredStrategyNames.Should().Equal("LocationWithX");
        diagnostic.ConfiguredStrategyIndexes.Should().Equal(2);
    }

    [Test]
    public void It_leaves_the_target_resource_unset_for_other_planner_failures()
    {
        var failure = BuildFailure(
            new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.NoApplicableRootSubject,
                _section,
                new ConfiguredAuthorizationStrategy("RelationshipsWithEdOrgsOnly", 0),
                RelationshipLocalOrder: 0
            )
        );

        failure.Diagnostics.Should().ContainSingle().Which.TargetResourceFullName.Should().BeNull();
    }
}

/// <summary>
/// DMS-1193 Task 47: the ReadChanges custom-view planning failures map to the change-query
/// security-configuration failure, whose message list is the only channel the ProblemDetails renders.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_RelationalReadGuardrails_ChangeQueryCustomViewFailures
{
    private static readonly QualifiedResourceName _section = new("Ed-Fi", "Section");

    [Test]
    public void It_reports_a_not_identifying_or_securable_basis_with_the_planner_hint()
    {
        string hint = CustomViewAuthorizationHintFormatter.FormatBasisNotIdentifyingOrSecurable(
            "locationReference",
            _section,
            new QualifiedResourceName("Ed-Fi", "Location")
        );

        var failure = RelationalReadGuardrails.BuildChangeQueryCustomViewSecurityConfigurationFailure([
            new RelationshipAuthorizationFailureMetadata(
                RelationshipAuthorizationFailureKind.CustomViewBasisNotIdentifyingOrSecurable,
                _section,
                new ConfiguredAuthorizationStrategy("LocationWithX", 0),
                RelationshipLocalOrder: 0,
                Location: new RelationshipAuthorizationFailureLocation(
                    JsonPath: "$.locationReference",
                    ReadableName: "locationReference",
                    AuthorizationObjectName: "auth.LocationWithX"
                ),
                Hint: hint
            ),
        ]);

        failure.UnavailableStrategyNames.Should().BeEmpty();
        failure
            .Errors.Should()
            .Equal(
                "Relational change query authorization metadata is invalid for resource 'Ed-Fi.Section'. "
                    + "Strategy 'LocationWithX' uses custom auth view 'auth.LocationWithX'. "
                    + hint
            );
        failure.Errors[0].Should().Contain("neither an identifying property nor a securable element");
    }

    [Test]
    public void It_reports_an_unknown_basis_resource_through_the_canonical_unknown_strategy_message()
    {
        var failure = RelationalReadGuardrails.BuildChangeQueryCustomViewSecurityConfigurationFailure([
            UnknownBasis("FooWithBar", 0),
        ]);

        failure.UnavailableStrategyNames.Should().Equal("FooWithBar");
        failure
            .Errors.Should()
            .Equal(SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies(["FooWithBar"]));
    }

    [Test]
    public void It_keeps_failure_order_and_collapses_unknown_bases_into_one_message()
    {
        var noJoinPath = new RelationshipAuthorizationFailureMetadata(
            RelationshipAuthorizationFailureKind.NoCustomViewJoinPath,
            _section,
            new ConfiguredAuthorizationStrategy("GradeWithSomething", 1),
            RelationshipLocalOrder: 1,
            Location: new RelationshipAuthorizationFailureLocation(
                AuthorizationObjectName: "auth.GradeWithSomething"
            ),
            Hint: "No DocumentId join path could be resolved from subject resource 'Ed-Fi.Section' to custom view basis resource 'Ed-Fi.Grade'."
        );

        var failure = RelationalReadGuardrails.BuildChangeQueryCustomViewSecurityConfigurationFailure([
            UnknownBasis("FooWithBar", 0),
            noJoinPath,
            UnknownBasis("BarWithBaz", 2),
        ]);

        failure.UnavailableStrategyNames.Should().Equal("FooWithBar", "BarWithBaz");
        failure
            .Errors.Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
                    "FooWithBar",
                    "BarWithBaz",
                ]),
                CustomViewAuthorizationFailureMessages.NoJoinPath(noJoinPath, "change query")
            );
        failure
            .Errors[1]
            .Should()
            .StartWith(
                "Relational change query authorization metadata is invalid for resource 'Ed-Fi.Section'."
            );
    }

    private static RelationshipAuthorizationFailureMetadata UnknownBasis(string strategyName, int index) =>
        new(
            RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource,
            _section,
            new ConfiguredAuthorizationStrategy(strategyName, index),
            RelationshipLocalOrder: index,
            Location: new RelationshipAuthorizationFailureLocation(
                AuthorizationObjectName: strategyName[
                    ..strategyName.IndexOf("With", StringComparison.Ordinal)
                ]
            )
        );
}
