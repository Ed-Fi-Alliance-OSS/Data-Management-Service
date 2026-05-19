// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common;

public sealed record RelationshipAuthorizationCrudScenario(
    string Name,
    string ResourceName,
    IReadOnlyList<string> StrategyNames
);

public static class RelationshipAuthorizationCrudTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/synthetic/authorization-query";
    public const string ProjectEndpointName = "authz";
    public const string MultiRootEdOrgResourceName = "AuthorizationAndResource";
    public const string RootAndChildEdOrgResourceName = "AuthorizationRootChildResource";
    public const string ChildOnlyEdOrgResourceName = "AuthorizationChildOnlyResource";
    public const string NullableRootEdOrgResourceName = "AuthorizationNullableResource";

    public const long ClaimEducationOrganizationId = 900;
    public const long AuthorizedSchoolId = 100;
    public const long SecondAuthorizedSchoolId = 200;
    public const long UnauthorizedSchoolId = 300;

    public const string RelationshipsWithEdOrgsOnly = "RelationshipsWithEdOrgsOnly";
    public const string RelationshipsWithEdOrgsOnlyInverted = "RelationshipsWithEdOrgsOnlyInverted";
    public const string NoFurtherAuthorizationRequired = "NoFurtherAuthorizationRequired";
    public const string NamespaceBased = "NamespaceBased";

    public static IReadOnlyList<string> EdOrgOnlyStrategyNames { get; } = [RelationshipsWithEdOrgsOnly];

    public static IReadOnlyList<string> InvertedEdOrgOnlyStrategyNames { get; } =
    [RelationshipsWithEdOrgsOnlyInverted];

    public static IReadOnlyList<string> NoFurtherAuthorizationRequiredOnlyStrategyNames { get; } =
    [NoFurtherAuthorizationRequired];

    public static IReadOnlyList<string> EdOrgOnlyPlusNoFurtherAuthorizationRequiredStrategyNames { get; } =
    [RelationshipsWithEdOrgsOnly, NoFurtherAuthorizationRequired];

    public static IReadOnlyList<string> EdOrgOnlyPlusKnownUnsupportedStrategyNames { get; } =
    [RelationshipsWithEdOrgsOnly, NamespaceBased];

    public static RelationshipAuthorizationCrudScenario SupportedEdOrgOnlyScenario { get; } =
        new("supported-edorg-only", RootAndChildEdOrgResourceName, EdOrgOnlyStrategyNames);

    public static RelationshipAuthorizationCrudScenario NoOpOnlyScenario { get; } =
        new(
            "no-further-authorization-required-only",
            RootAndChildEdOrgResourceName,
            NoFurtherAuthorizationRequiredOnlyStrategyNames
        );

    public static RelationshipAuthorizationCrudScenario SupportedPlusNoOpScenario { get; } =
        new(
            "supported-edorg-only-plus-no-further-authorization-required",
            RootAndChildEdOrgResourceName,
            EdOrgOnlyPlusNoFurtherAuthorizationRequiredStrategyNames
        );

    public static RelationshipAuthorizationCrudScenario SupportedPlusKnownUnsupportedScenario { get; } =
        new(
            "supported-edorg-only-plus-known-unsupported",
            RootAndChildEdOrgResourceName,
            EdOrgOnlyPlusKnownUnsupportedStrategyNames
        );

    public static RelationshipAuthorizationCrudScenario SecurityConfigurationPlusKnownUnsupportedScenario { get; } =
        new(
            "security-configuration-plus-known-unsupported",
            ChildOnlyEdOrgResourceName,
            EdOrgOnlyPlusKnownUnsupportedStrategyNames
        );

    public static RelationshipAuthorizationCrudScenario InvertedEdOrgOnlyScenario { get; } =
        new("inverted-edorg-only", RootAndChildEdOrgResourceName, InvertedEdOrgOnlyStrategyNames);

    public static IReadOnlyList<RelationshipAuthorizationCrudScenario> StrategyScenarios { get; } =
    [
        SupportedEdOrgOnlyScenario,
        NoOpOnlyScenario,
        SupportedPlusNoOpScenario,
        SupportedPlusKnownUnsupportedScenario,
        SecurityConfigurationPlusKnownUnsupportedScenario,
        InvertedEdOrgOnlyScenario,
    ];
}
