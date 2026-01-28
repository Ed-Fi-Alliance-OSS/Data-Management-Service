// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Unordered_Derived_Collections
{
    private DerivedRelationalModelSet _derivedModelSet = default!;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new PopulateUnorderedCollectionsPass(effectiveSchemaSet) }
        );

        _derivedModelSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    [Test]
    public void It_should_order_abstract_identity_tables_by_project_then_resource()
    {
        var orderedResources = _derivedModelSet
            .AbstractIdentityTablesInNameOrder.Select(table => table.AbstractResourceKey.Resource)
            .ToArray();

        orderedResources
            .Should()
            .Equal(
                new QualifiedResourceName("Ed-Fi", "School"),
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"),
                new QualifiedResourceName("Sample", "Section")
            );
    }

    [Test]
    public void It_should_order_abstract_union_views_by_project_then_resource()
    {
        var orderedResources = _derivedModelSet
            .AbstractUnionViewsInNameOrder.Select(view => view.AbstractResourceKey.Resource)
            .ToArray();

        orderedResources
            .Should()
            .Equal(
                new QualifiedResourceName("Ed-Fi", "School"),
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"),
                new QualifiedResourceName("Sample", "Section")
            );
    }

    [Test]
    public void It_should_order_indexes_by_table_then_name()
    {
        var orderedIndexes = _derivedModelSet
            .IndexesInCreateOrder.Select(index =>
                $"{index.Table.Schema.Value}.{index.Table.Name}:{index.Name.Value}"
            )
            .ToArray();

        orderedIndexes
            .Should()
            .Equal(
                "edfi.School:IX_School_A",
                "edfi.School:IX_School_B",
                "edfi.SchoolTypeDescriptor:IX_SchoolTypeDescriptor",
                "sample.Section:IX_Section_B"
            );
    }

    [Test]
    public void It_should_order_triggers_by_table_then_name()
    {
        var orderedTriggers = _derivedModelSet
            .TriggersInCreateOrder.Select(trigger =>
                $"{trigger.Table.Schema.Value}.{trigger.Table.Name}:{trigger.Name.Value}"
            )
            .ToArray();

        orderedTriggers
            .Should()
            .Equal(
                "edfi.School:TR_School_A",
                "edfi.School:TR_School_B",
                "edfi.SchoolTypeDescriptor:TR_SchoolTypeDescriptor",
                "sample.Section:TR_Section_B"
            );
    }

    private sealed class PopulateUnorderedCollectionsPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _school;
        private readonly ResourceKeyEntry _schoolTypeDescriptor;
        private readonly ResourceKeyEntry _section;

        public PopulateUnorderedCollectionsPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            _school = FindResourceKey(effectiveSchemaSet, "Ed-Fi", "School");
            _schoolTypeDescriptor = FindResourceKey(effectiveSchemaSet, "Ed-Fi", "SchoolTypeDescriptor");
            _section = FindResourceKey(effectiveSchemaSet, "Sample", "Section");
        }

        public int Order { get; } = 1;

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var edfiSchema = new DbSchemaName("edfi");
            var sampleSchema = new DbSchemaName("sample");

            context.AbstractIdentityTablesInNameOrder.Add(
                new AbstractIdentityTableInfo(
                    _section,
                    new DbTableName(sampleSchema, "SectionIdentity"),
                    [],
                    []
                )
            );
            context.AbstractIdentityTablesInNameOrder.Add(
                new AbstractIdentityTableInfo(
                    _schoolTypeDescriptor,
                    new DbTableName(edfiSchema, "SchoolTypeDescriptorIdentity"),
                    [],
                    []
                )
            );
            context.AbstractIdentityTablesInNameOrder.Add(
                new AbstractIdentityTableInfo(_school, new DbTableName(edfiSchema, "SchoolIdentity"), [], [])
            );

            context.AbstractUnionViewsInNameOrder.Add(
                new AbstractUnionViewInfo(_section, new DbTableName(sampleSchema, "Section_View"), [])
            );
            context.AbstractUnionViewsInNameOrder.Add(
                new AbstractUnionViewInfo(
                    _schoolTypeDescriptor,
                    new DbTableName(edfiSchema, "SchoolTypeDescriptor_View"),
                    []
                )
            );
            context.AbstractUnionViewsInNameOrder.Add(
                new AbstractUnionViewInfo(_school, new DbTableName(edfiSchema, "School_View"), [])
            );

            context.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName("IX_Section_B"),
                    new DbTableName(sampleSchema, "Section"),
                    [],
                    false,
                    DbIndexKind.ForeignKeySupport
                )
            );
            context.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName("IX_School_B"),
                    new DbTableName(edfiSchema, "School"),
                    [],
                    false,
                    DbIndexKind.ForeignKeySupport
                )
            );
            context.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName("IX_SchoolTypeDescriptor"),
                    new DbTableName(edfiSchema, "SchoolTypeDescriptor"),
                    [],
                    false,
                    DbIndexKind.ForeignKeySupport
                )
            );
            context.IndexInventory.Add(
                new DbIndexInfo(
                    new DbIndexName("IX_School_A"),
                    new DbTableName(edfiSchema, "School"),
                    [],
                    false,
                    DbIndexKind.ForeignKeySupport
                )
            );

            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_Section_B"),
                    new DbTableName(sampleSchema, "Section"),
                    DbTriggerKind.DocumentStamping,
                    []
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_B"),
                    new DbTableName(edfiSchema, "School"),
                    DbTriggerKind.DocumentStamping,
                    []
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_SchoolTypeDescriptor"),
                    new DbTableName(edfiSchema, "SchoolTypeDescriptor"),
                    DbTriggerKind.DocumentStamping,
                    []
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_A"),
                    new DbTableName(edfiSchema, "School"),
                    DbTriggerKind.DocumentStamping,
                    []
                )
            );
        }

        private static ResourceKeyEntry FindResourceKey(
            EffectiveSchemaSet effectiveSchemaSet,
            string projectName,
            string resourceName
        )
        {
            ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

            var resourceKey = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Single(entry =>
                entry.Resource.ProjectName == projectName && entry.Resource.ResourceName == resourceName
            );

            return resourceKey;
        }
    }
}
