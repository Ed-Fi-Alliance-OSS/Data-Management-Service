// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

public static class CustomViewAuthorizationPlanner
{
    private static readonly DbSchemaName AuthSchema = new("auth");
    private static readonly DbColumnName DocumentIdColumn = new("DocumentId");

    public static CustomViewAuthorizationPlanOutcome Plan(
        MappingSet mappingSet,
        ConcreteResourceModel resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(customViewStrategies);

        var checks = new List<CustomViewAuthorizationCheckSpec>();
        var failures = new List<RelationshipAuthorizationFailureMetadata>();
        var subjectResource = resource.RelationalModel.Resource;
        var rootTable = resource.RelationalModel.Root.Table;

        foreach (var strategy in customViewStrategies)
        {
            var path = SecurableElementColumnPathResolver.ResolveSecurableElementColumnPath(
                subjectResource,
                strategy.BasisResource,
                mappingSet.Model
            );

            if (path.Count == 0)
            {
                failures.Add(
                    new RelationshipAuthorizationFailureMetadata(
                        RelationshipAuthorizationFailureKind.NoCustomViewJoinPath,
                        subjectResource,
                        strategy.ConfiguredStrategy,
                        strategy.AuthorizationLocalOrder,
                        Location: new RelationshipAuthorizationFailureLocation(
                            AuthorizationObjectName: $"auth.{strategy.ConfiguredStrategy.StrategyName}"
                        ),
                        Hint: $"No DocumentId join path could be resolved from subject resource '{subjectResource.ProjectName}.{subjectResource.ResourceName}' to custom view basis resource '{strategy.BasisResource.ProjectName}.{strategy.BasisResource.ResourceName}'."
                    )
                );

                continue;
            }

            checks.Add(
                new CustomViewAuthorizationCheckSpec(
                    strategy.ConfiguredStrategy,
                    rootTable,
                    DocumentIdColumn,
                    new DbTableName(AuthSchema, strategy.ConfiguredStrategy.StrategyName),
                    DocumentIdColumn,
                    path
                )
            );
        }

        // Successfully planned checks are carried on the failure outcome too: they are what lets the caller
        // validate the custom views configured ahead of the earliest failure before reporting it.
        return failures.Count > 0
            ? new CustomViewAuthorizationPlanOutcome.SecurityConfiguration(failures, checks)
            : new CustomViewAuthorizationPlanOutcome.Plan(checks);
    }
}
