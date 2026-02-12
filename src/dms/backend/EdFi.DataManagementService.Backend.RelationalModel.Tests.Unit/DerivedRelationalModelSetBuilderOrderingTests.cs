// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for unordered derived collections.
/// </summary>
[TestFixture]
public class Given_Unordered_Derived_Collections
{
    private DerivedRelationalModelSet _derivedModelSet = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new PopulateUnorderedCollectionsPass(effectiveSchemaSet) }
        );

        _derivedModelSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should order abstract identity tables by project then resource.
    /// </summary>
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

    /// <summary>
    /// It should order abstract union views by project then resource.
    /// </summary>
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

    /// <summary>
    /// It should order indexes by table then name.
    /// </summary>
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

    /// <summary>
    /// It should order triggers by table then name.
    /// </summary>
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

    /// <summary>
    /// Test type populate unordered collections pass.
    /// </summary>
    private sealed class PopulateUnorderedCollectionsPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _school;
        private readonly ResourceKeyEntry _schoolTypeDescriptor;
        private readonly ResourceKeyEntry _section;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public PopulateUnorderedCollectionsPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            _school = FindResourceKey(effectiveSchemaSet, "Ed-Fi", "School");
            _schoolTypeDescriptor = FindResourceKey(effectiveSchemaSet, "Ed-Fi", "SchoolTypeDescriptor");
            _section = FindResourceKey(effectiveSchemaSet, "Sample", "Section");
        }

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var edfiSchema = new DbSchemaName("edfi");
            var sampleSchema = new DbSchemaName("sample");

            context.AbstractIdentityTablesInNameOrder.Add(
                BuildAbstractIdentityTable(_section, sampleSchema, "SectionIdentity")
            );
            context.AbstractIdentityTablesInNameOrder.Add(
                BuildAbstractIdentityTable(_schoolTypeDescriptor, edfiSchema, "SchoolTypeDescriptorIdentity")
            );
            context.AbstractIdentityTablesInNameOrder.Add(
                BuildAbstractIdentityTable(_school, edfiSchema, "SchoolIdentity")
            );

            context.AbstractUnionViewsInNameOrder.Add(
                BuildAbstractUnionView(_section, _section, sampleSchema, "Section_View", "Section")
            );
            context.AbstractUnionViewsInNameOrder.Add(
                BuildAbstractUnionView(
                    _schoolTypeDescriptor,
                    _schoolTypeDescriptor,
                    edfiSchema,
                    "SchoolTypeDescriptor_View",
                    "SchoolTypeDescriptor"
                )
            );
            context.AbstractUnionViewsInNameOrder.Add(
                BuildAbstractUnionView(_school, _school, edfiSchema, "School_View", "School")
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
                    [],
                    []
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_B"),
                    new DbTableName(edfiSchema, "School"),
                    DbTriggerKind.DocumentStamping,
                    [],
                    []
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_SchoolTypeDescriptor"),
                    new DbTableName(edfiSchema, "SchoolTypeDescriptor"),
                    DbTriggerKind.DocumentStamping,
                    [],
                    []
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_A"),
                    new DbTableName(edfiSchema, "School"),
                    DbTriggerKind.DocumentStamping,
                    [],
                    []
                )
            );
        }

        /// <summary>
        /// Build abstract identity table.
        /// </summary>
        private static AbstractIdentityTableInfo BuildAbstractIdentityTable(
            ResourceKeyEntry resourceKey,
            DbSchemaName schema,
            string tableName
        )
        {
            var jsonScope = JsonPathExpressionCompiler.FromSegments([]);
            var key = new TableKey(
                $"PK_{tableName}",
                new[]
                {
                    new DbKeyColumn(RelationalNameConventions.DocumentIdColumnName, ColumnKind.ParentKeyPart),
                }
            );
            DbColumnModel[] columns =
            [
                new DbColumnModel(
                    RelationalNameConventions.DocumentIdColumnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ];

            var table = new DbTableModel(
                new DbTableName(schema, tableName),
                jsonScope,
                key,
                columns,
                Array.Empty<TableConstraint>()
            );

            return new AbstractIdentityTableInfo(resourceKey, table);
        }

        /// <summary>
        /// Build abstract union view.
        /// </summary>
        private static AbstractUnionViewInfo BuildAbstractUnionView(
            ResourceKeyEntry abstractResourceKey,
            ResourceKeyEntry concreteMemberResourceKey,
            DbSchemaName schema,
            string viewName,
            string tableName
        )
        {
            return new AbstractUnionViewInfo(
                abstractResourceKey,
                new DbTableName(schema, viewName),
                new[]
                {
                    new AbstractUnionViewOutputColumn(
                        RelationalNameConventions.DocumentIdColumnName,
                        new RelationalScalarType(ScalarKind.Int64),
                        SourceJsonPath: null,
                        TargetResource: null
                    ),
                },
                new[]
                {
                    new AbstractUnionViewArm(
                        concreteMemberResourceKey,
                        new DbTableName(schema, tableName),
                        new AbstractUnionViewProjectionExpression[]
                        {
                            new AbstractUnionViewProjectionExpression.SourceColumn(
                                RelationalNameConventions.DocumentIdColumnName
                            ),
                        }
                    ),
                }
            );
        }

        /// <summary>
        /// Find resource key.
        /// </summary>
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
