// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Identity_Dependencies_Visited_Before_Their_Targets
{
    [TestCase(SqlDialect.Pgsql, true)]
    [TestCase(SqlDialect.Pgsql, false)]
    [TestCase(SqlDialect.Mssql, true)]
    [TestCase(SqlDialect.Mssql, false)]
    public void It_propagates_mutability_through_every_identity_hop_only(
        SqlDialect dialect,
        bool originIsMutable
    )
    {
        // Name ordering visits Alpha and Bravo before their targets can become mutable.
        // Observer has an ordinary reference, so its consumers must remain non-cascading.
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new("Alpha", IdentityScalars: ["alphaKey"], References: [new("Bravo", PartOfIdentity: true)]),
            new("Bravo", IdentityScalars: ["bravoKey"], References: [new("Charlie", PartOfIdentity: true)]),
            new(
                "Charlie",
                IdentityScalars: ["charlieKey"],
                References: [new("ZOrigin", PartOfIdentity: true)]
            ),
            new("Observer", IdentityScalars: ["observerKey"], References: [new("Alpha")]),
            new("ObserverConsumer", IdentityScalars: ["consumerKey"], References: [new("Observer")]),
            new("Isolated", IdentityScalars: ["isolatedKey"]),
            new("IsolatedConsumer", IdentityScalars: ["isolatedConsumerKey"], References: [new("Isolated")]),
            new("ZOrigin", AllowIdentityUpdates: originIsMutable, IdentityScalars: ["originKey"])
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new TransitiveIdentityMutabilityPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
        ]);
        ISqlDialectRules rules =
            dialect is SqlDialect.Pgsql ? new PgsqlDialectRules() : new MssqlDialectRules();

        var result = builder.Build(schemaSet, dialect, rules);

        var referenceActions = result
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource
                    .RelationalModel.Root.Constraints.OfType<TableConstraint.ForeignKey>()
                    .Where(foreignKey => foreignKey.TargetTable.Schema.Value == "edfi")
                    .Select(foreignKey =>
                        (
                            Resource: resource.ResourceKey.Resource.ResourceName,
                            Target: foreignKey.TargetTable.Name,
                            foreignKey.OnUpdate
                        )
                    )
            )
            .ToArray();
        var propagatedAction = originIsMutable ? ReferentialAction.Cascade : ReferentialAction.NoAction;
        referenceActions
            .Should()
            .Equal(
                ("Alpha", "Bravo", propagatedAction),
                ("Bravo", "Charlie", propagatedAction),
                ("Charlie", "ZOrigin", propagatedAction),
                ("IsolatedConsumer", "Isolated", ReferentialAction.NoAction),
                ("Observer", "Alpha", propagatedAction),
                ("ObserverConsumer", "Observer", ReferentialAction.NoAction)
            );
    }
}
