// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for trigger set composition with nested collections, references, and abstract resources.
/// </summary>
[TestFixture]
public class Given_Trigger_Set_Composition
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger for root table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_root_table()
    {
        var schoolStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "School" && t.Kind == DbTriggerKind.DocumentStamping
        );

        schoolStamp.Should().NotBeNull();
        schoolStamp!.Name.Value.Should().Be("TR_School_Stamp");
        schoolStamp.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should include identity projection columns on root table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_include_identity_projection_columns_on_root_table_stamping_trigger()
    {
        var schoolStamp = _triggers.Single(t =>
            t.Table.Name == "School" && t.Kind == DbTriggerKind.DocumentStamping
        );

        schoolStamp.IdentityProjectionColumns.Should().NotBeEmpty();
        schoolStamp
            .IdentityProjectionColumns.Select(c => c.Value)
            .Should()
            .Contain("EducationOrganizationId");
    }

    /// <summary>
    /// It should create DocumentStamping trigger for child table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_child_table()
    {
        var childStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.Should().NotBeNull();
        childStamp!.Name.Value.Should().Be("TR_SchoolAddress_Stamp");
    }

    /// <summary>
    /// It should use root document ID as KeyColumns on child table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_use_root_document_ID_as_KeyColumns_on_child_table_stamping_trigger()
    {
        var childStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.KeyColumns.Should().ContainSingle();
        childStamp.KeyColumns[0].Value.Should().Contain("DocumentId");
    }

    /// <summary>
    /// It should have empty identity projection columns on child table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_have_empty_identity_projection_columns_on_child_table_stamping_trigger()
    {
        var childStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.IdentityProjectionColumns.Should().BeEmpty();
    }

    /// <summary>
    /// It should create ReferentialIdentityMaintenance trigger on root table.
    /// </summary>
    [Test]
    public void It_should_create_ReferentialIdentityMaintenance_trigger_on_root_table()
    {
        var refIdentity = _triggers.SingleOrDefault(t =>
            t.Table.Name == "School" && t.Kind == DbTriggerKind.ReferentialIdentityMaintenance
        );

        refIdentity.Should().NotBeNull();
        refIdentity!.Name.Value.Should().Be("TR_School_ReferentialIdentity");
        refIdentity.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
        refIdentity.IdentityProjectionColumns.Should().NotBeEmpty();
    }

    /// <summary>
    /// It should create AbstractIdentityMaintenance trigger for subclass resource.
    /// </summary>
    [Test]
    public void It_should_create_AbstractIdentityMaintenance_trigger_for_subclass_resource()
    {
        var abstractMaintenance = _triggers.SingleOrDefault(t =>
            t.Table.Name == "School" && t.Kind == DbTriggerKind.AbstractIdentityMaintenance
        );

        abstractMaintenance.Should().NotBeNull();
        abstractMaintenance!.Name.Value.Should().Be("TR_School_AbstractIdentity");
        abstractMaintenance.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
        abstractMaintenance.TargetTable.Should().NotBeNull();
        abstractMaintenance.TargetTable!.Value.Name.Should().Be("EducationOrganizationIdentity");
    }
}

/// <summary>
/// Test fixture for extension table trigger derivation.
/// </summary>
[TestFixture]
public class Given_Extension_Table_Triggers
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProjectSchema = TriggerInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger on extension table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_on_extension_table()
    {
        var extensionStampTriggers = _triggers.Where(t =>
            t.Table.Name.Contains("Extension") && t.Kind == DbTriggerKind.DocumentStamping
        );

        extensionStampTriggers.Should().NotBeEmpty();
    }

    /// <summary>
    /// It should use DocumentId as KeyColumns on extension table stamping trigger.
    /// </summary>
    [Test]
    public void It_should_use_DocumentId_as_KeyColumns_on_extension_table_stamping_trigger()
    {
        var extensionStamp = _triggers.First(t =>
            t.Table.Name.Contains("Extension") && t.Kind == DbTriggerKind.DocumentStamping
        );

        extensionStamp.KeyColumns.Should().ContainSingle();
        extensionStamp.KeyColumns[0].Value.Should().Be("DocumentId");
    }
}

/// <summary>
/// Test fixture for descriptor resource trigger exclusion.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Trigger_Derivation
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should not derive triggers for descriptor resources.
    /// </summary>
    [Test]
    public void It_should_not_derive_triggers_for_descriptor_resources()
    {
        _triggers.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for IdentityPropagationFallback stub.
/// </summary>
[TestFixture]
public class Given_IdentityPropagationFallback_Stub
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should not emit any IdentityPropagationFallback triggers.
    /// </summary>
    [Test]
    public void It_should_not_emit_any_IdentityPropagationFallback_triggers()
    {
        var fallbackTriggers = _triggers.Where(t => t.Kind == DbTriggerKind.IdentityPropagationFallback);

        fallbackTriggers.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for three-level nested collection trigger derivation.
/// </summary>
[TestFixture]
public class Given_Three_Level_Nested_Collections
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildThreeLevelNestedProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger for grandchild table.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_grandchild_table()
    {
        var grandchildStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        grandchildStamp.Should().NotBeNull();
        grandchildStamp!.Name.Value.Should().Be("TR_SchoolAddressPeriod_Stamp");
    }

    /// <summary>
    /// It should use root document ID as KeyColumns on grandchild table.
    /// </summary>
    [Test]
    public void It_should_use_root_document_ID_as_KeyColumns_on_grandchild_table()
    {
        var grandchildStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        grandchildStamp.KeyColumns.Should().ContainSingle();
        grandchildStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
    }

    /// <summary>
    /// It should have empty identity projection columns on grandchild table.
    /// </summary>
    [Test]
    public void It_should_have_empty_identity_projection_columns_on_grandchild_table()
    {
        var grandchildStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        grandchildStamp.IdentityProjectionColumns.Should().BeEmpty();
    }

    /// <summary>
    /// It should create DocumentStamping triggers for all three levels.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_triggers_for_all_three_levels()
    {
        var stampTriggers = _triggers.Where(t => t.Kind == DbTriggerKind.DocumentStamping);

        stampTriggers.Should().HaveCount(3);
    }

    /// <summary>
    /// It should use same root document ID column across all child levels.
    /// </summary>
    [Test]
    public void It_should_use_same_root_document_ID_column_across_all_child_levels()
    {
        var childStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );
        var grandchildStamp = _triggers.Single(t =>
            t.Table.Name == "SchoolAddressPeriod" && t.Kind == DbTriggerKind.DocumentStamping
        );

        childStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
        grandchildStamp.KeyColumns[0].Value.Should().Be("School_DocumentId");
    }
}

/// <summary>
/// Test fixture for multiple sibling collection trigger derivation.
/// </summary>
[TestFixture]
public class Given_Multiple_Sibling_Collections
{
    private IReadOnlyList<DbTriggerInfo> _triggers = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildSiblingCollectionsProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _triggers = result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should create DocumentStamping trigger for each sibling collection.
    /// </summary>
    [Test]
    public void It_should_create_DocumentStamping_trigger_for_each_sibling_collection()
    {
        var addressStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "StudentAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );
        var telephoneStamp = _triggers.SingleOrDefault(t =>
            t.Table.Name == "StudentTelephone" && t.Kind == DbTriggerKind.DocumentStamping
        );

        addressStamp.Should().NotBeNull();
        telephoneStamp.Should().NotBeNull();
    }

    /// <summary>
    /// It should produce distinct trigger names for sibling tables.
    /// </summary>
    [Test]
    public void It_should_produce_distinct_trigger_names_for_sibling_tables()
    {
        var addressStamp = _triggers.Single(t =>
            t.Table.Name == "StudentAddress" && t.Kind == DbTriggerKind.DocumentStamping
        );
        var telephoneStamp = _triggers.Single(t =>
            t.Table.Name == "StudentTelephone" && t.Kind == DbTriggerKind.DocumentStamping
        );

        addressStamp.Name.Value.Should().Be("TR_StudentAddress_Stamp");
        telephoneStamp.Name.Value.Should().Be("TR_StudentTelephone_Stamp");
    }

    /// <summary>
    /// It should create three DocumentStamping triggers total.
    /// </summary>
    [Test]
    public void It_should_create_three_DocumentStamping_triggers_total()
    {
        var stampTriggers = _triggers.Where(t => t.Kind == DbTriggerKind.DocumentStamping);

        stampTriggers.Should().HaveCount(3);
    }
}

/// <summary>
/// Test fixture for deterministic trigger ordering.
/// </summary>
[TestFixture]
public class Given_Deterministic_Trigger_Ordering
{
    private IReadOnlyList<DbTriggerInfo> _triggersFirst = default!;
    private IReadOnlyList<DbTriggerInfo> _triggersSecond = default!;

    /// <summary>
    /// Sets up the test fixture by running the pipeline twice.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _triggersFirst = BuildTriggers();
        _triggersSecond = BuildTriggers();
    }

    private static IReadOnlyList<DbTriggerInfo> BuildTriggers()
    {
        var coreProjectSchema = TriggerInventoryTestSchemaBuilder.BuildCompositeProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            TriggerInventoryTestSchemaBuilder.BuildPassesThroughTriggerDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        return result.TriggersInCreateOrder;
    }

    /// <summary>
    /// It should produce identical trigger sequence on repeated builds.
    /// </summary>
    [Test]
    public void It_should_produce_identical_trigger_sequence_on_repeated_builds()
    {
        var firstSequence = _triggersFirst.Select(t => (t.Table.Name, t.Name.Value, t.Kind)).ToList();
        var secondSequence = _triggersSecond.Select(t => (t.Table.Name, t.Name.Value, t.Kind)).ToList();

        firstSequence.Should().Equal(secondSequence);
    }
}

/// <summary>
/// Test schema builder for trigger inventory pass tests.
/// </summary>
internal static class TriggerInventoryTestSchemaBuilder
{
    /// <summary>
    /// Build the standard pass list through trigger derivation.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughTriggerDerivation()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new DeriveIndexInventoryPass(),
            new DeriveTriggerInventoryPass(),
        ];
    }

    /// <summary>
    /// Build project schema with nested collections, references, and an abstract resource.
    /// </summary>
    internal static JsonObject BuildCompositeProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSubclassSchoolWithAddressesSchema() },
        };
    }

    /// <summary>
    /// Build core project schema for extension testing.
    /// </summary>
    internal static JsonObject BuildExtensionCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildContactSchema() },
        };
    }

    /// <summary>
    /// Build extension project schema for extension testing.
    /// </summary>
    internal static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildContactExtensionSchema() },
        };
    }

    /// <summary>
    /// Build project schema with only a descriptor.
    /// </summary>
    internal static JsonObject BuildDescriptorOnlyProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["gradeLevelDescriptors"] = BuildDescriptorSchema() },
        };
    }

    /// <summary>
    /// Build project schema with three-level nested collections (School not a subclass).
    /// </summary>
    internal static JsonObject BuildThreeLevelNestedProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSchoolWithAddressPeriodsSchema() },
        };
    }

    /// <summary>
    /// Build project schema with sibling collections.
    /// </summary>
    internal static JsonObject BuildSiblingCollectionsProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["students"] = BuildStudentWithSiblingCollectionsSchema(),
            },
        };
    }

    private static JsonObject BuildSubclassSchoolWithAddressesSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 150,
                            },
                            ["city"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                        },
                    },
                },
            },
            ["required"] = new JsonArray("educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["isSubclass"] = true,
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildContactSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["contactUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
            },
            ["required"] = new JsonArray("contactUniqueId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.contactUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["ContactUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.contactUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildContactExtensionSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildDescriptorSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
            },
            ["required"] = new JsonArray("namespace", "codeValue"),
        };

        return new JsonObject
        {
            ["resourceName"] = "GradeLevelDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildSchoolWithAddressPeriodsSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 150,
                            },
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["beginDate"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["format"] = "date",
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            ["required"] = new JsonArray("educationOrganizationId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildStudentWithSiblingCollectionsSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 150,
                            },
                        },
                    },
                },
                ["telephones"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["telephoneNumber"] = new JsonObject { ["type"] = "string", ["maxLength"] = 24 },
                        },
                    },
                },
            },
            ["required"] = new JsonArray("studentUniqueId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StudentUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.studentUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
