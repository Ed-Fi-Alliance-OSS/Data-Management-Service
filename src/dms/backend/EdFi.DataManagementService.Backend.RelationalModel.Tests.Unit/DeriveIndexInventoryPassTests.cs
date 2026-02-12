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
/// Test fixture for FK leftmost-prefix suppression in index derivation.
/// </summary>
[TestFixture]
public class Given_FK_Columns_Covered_By_PK
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildSchoolWithReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should suppress FK-support index when PK covers FK columns.
    /// </summary>
    [Test]
    public void It_should_suppress_FK_support_index_when_PK_covers_FK_columns()
    {
        // The Enrollment root table has PK on DocumentId and FK to dms.Document on DocumentId.
        // The FK columns [DocumentId] are a leftmost prefix of PK [DocumentId], so the
        // FK-support index should be suppressed.
        var enrollmentFkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment"
            && index.Kind == DbIndexKind.ForeignKeySupport
            && index.KeyColumns.Count == 1
            && index.KeyColumns[0].Value == "DocumentId"
        );

        enrollmentFkIndexes.Should().BeEmpty("FK on DocumentId is covered by PK on DocumentId");
    }

    /// <summary>
    /// It should create PK-implied index for each table.
    /// </summary>
    [Test]
    public void It_should_create_PK_implied_index_for_each_table()
    {
        var enrollmentPkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.PrimaryKey
        );

        enrollmentPkIndexes.Should().ContainSingle();
        var pk = enrollmentPkIndexes.Single();
        pk.IsUnique.Should().BeTrue();
        pk.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should create UK-implied index for natural key.
    /// </summary>
    [Test]
    public void It_should_create_UK_implied_index_for_natural_key()
    {
        var enrollmentUkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.UniqueConstraint
        );

        enrollmentUkIndexes.Should().ContainSingle();
        var uk = enrollmentUkIndexes.Single();
        uk.IsUnique.Should().BeTrue();
        uk.KeyColumns.Select(c => c.Value).Should().Equal("School_DocumentId", "Student_DocumentId");
    }

    /// <summary>
    /// It should create FK-support indexes for non-covered FK columns.
    /// </summary>
    [Test]
    public void It_should_create_FK_support_indexes_for_non_covered_FK_columns()
    {
        // The Enrollment table has FKs to School and Student (via DocumentId columns).
        // These are NOT covered by PK [DocumentId] so they should produce FK-support indexes.
        var enrollmentFkIndexes = _indexes
            .Where(index => index.Table.Name == "Enrollment" && index.Kind == DbIndexKind.ForeignKeySupport)
            .ToArray();

        enrollmentFkIndexes.Should().NotBeEmpty();

        foreach (var fkIndex in enrollmentFkIndexes)
        {
            fkIndex.IsUnique.Should().BeFalse();
            fkIndex.Name.Value.Should().StartWith("IX_Enrollment_");
        }
    }
}

/// <summary>
/// Test fixture for composite FK index naming.
/// </summary>
[TestFixture]
public class Given_Composite_FK_Index
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildSchoolWithReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should name composite FK index with all column names.
    /// </summary>
    [Test]
    public void It_should_name_composite_FK_index_with_all_column_names()
    {
        // The reference FK to School has two identity columns, producing a composite FK
        // with columns like [School_DocumentId]. Single-column FKs should be named IX_{Table}_{Col}.
        var schoolFkIndexes = _indexes.Where(index =>
            index.Table.Name == "Enrollment"
            && index.Kind == DbIndexKind.ForeignKeySupport
            && index.KeyColumns.Any(c => c.Value.Contains("School"))
        );

        foreach (var fkIndex in schoolFkIndexes)
        {
            fkIndex.Name.Value.Should().StartWith("IX_Enrollment_");
            fkIndex.IsUnique.Should().BeFalse();
        }
    }
}

/// <summary>
/// Test fixture for abstract identity table indexes.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_Indexes
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildAbstractResourceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should derive PK index for abstract identity table.
    /// </summary>
    [Test]
    public void It_should_derive_PK_index_for_abstract_identity_table()
    {
        var identityTablePkIndexes = _indexes.Where(index =>
            index.Table.Name == "EducationOrganizationIdentity" && index.Kind == DbIndexKind.PrimaryKey
        );

        identityTablePkIndexes.Should().ContainSingle();
        var pk = identityTablePkIndexes.Single();
        pk.IsUnique.Should().BeTrue();
        pk.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should derive UK indexes for abstract identity table.
    /// </summary>
    [Test]
    public void It_should_derive_UK_indexes_for_abstract_identity_table()
    {
        var identityTableUkIndexes = _indexes.Where(index =>
            index.Table.Name == "EducationOrganizationIdentity" && index.Kind == DbIndexKind.UniqueConstraint
        );

        identityTableUkIndexes.Should().NotBeEmpty();

        foreach (var ukIndex in identityTableUkIndexes)
        {
            ukIndex.IsUnique.Should().BeTrue();
        }
    }
}

/// <summary>
/// Test fixture for descriptor resource index exclusion.
/// </summary>
[TestFixture]
public class Given_Descriptor_Resources_For_Index_Derivation
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildDescriptorOnlyProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should not derive indexes for descriptor resources.
    /// </summary>
    [Test]
    public void It_should_not_derive_indexes_for_descriptor_resources()
    {
        _indexes.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for extension table index derivation.
/// </summary>
[TestFixture]
public class Given_Extension_Table_Indexes
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildExtensionCoreProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProjectSchema = IndexInventoryTestSchemaBuilder.BuildExtensionProjectSchema();
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should derive PK index for extension table.
    /// </summary>
    [Test]
    public void It_should_derive_PK_index_for_extension_table()
    {
        var extensionPkIndexes = _indexes.Where(index =>
            index.Table.Name == "ContactExtension" && index.Kind == DbIndexKind.PrimaryKey
        );

        extensionPkIndexes.Should().ContainSingle();
        var pk = extensionPkIndexes.Single();
        pk.IsUnique.Should().BeTrue();
        pk.KeyColumns.Select(c => c.Value).Should().Equal("DocumentId");
    }

    /// <summary>
    /// It should suppress FK support index when extension FK covered by PK.
    /// </summary>
    [Test]
    public void It_should_suppress_FK_support_index_when_extension_FK_covered_by_PK()
    {
        var extensionFkIndexes = _indexes.Where(index =>
            index.Table.Name == "ContactExtension"
            && index.Kind == DbIndexKind.ForeignKeySupport
            && index.KeyColumns.Count == 1
            && index.KeyColumns[0].Value == "DocumentId"
        );

        extensionFkIndexes.Should().BeEmpty("FK on DocumentId is covered by PK on DocumentId");
    }

    /// <summary>
    /// It should name extension PK index correctly.
    /// </summary>
    [Test]
    public void It_should_name_extension_PK_index_correctly()
    {
        var extensionPk = _indexes.Single(index =>
            index.Table.Name == "ContactExtension" && index.Kind == DbIndexKind.PrimaryKey
        );

        extensionPk.Name.Value.Should().StartWith("PK_ContactExtension");
    }
}

/// <summary>
/// Test fixture for long index name validation.
/// </summary>
[TestFixture]
public class Given_Long_Index_Names_After_Derivation
{
    private IReadOnlyList<DbIndexInfo> _indexes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = IndexInventoryTestSchemaBuilder.BuildLongNameResourceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            IndexInventoryTestSchemaBuilder.BuildPassesThroughIndexDerivation()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _indexes = result.IndexesInCreateOrder;
    }

    /// <summary>
    /// It should complete derivation without name collision.
    /// </summary>
    [Test]
    public void It_should_complete_derivation_without_name_collision()
    {
        _indexes.Should().NotBeEmpty();
    }

    /// <summary>
    /// It should produce FK support index with long name.
    /// </summary>
    [Test]
    public void It_should_produce_FK_support_index_with_long_name()
    {
        var fkIndexes = _indexes.Where(index =>
            index.Table.Name == "StudentEducationOrganizationAssociation"
            && index.Kind == DbIndexKind.ForeignKeySupport
        );

        fkIndexes.Should().NotBeEmpty();
        fkIndexes
            .Should()
            .AllSatisfy(index =>
                index.Name.Value.Should().StartWith("IX_StudentEducationOrganizationAssociation_")
            );
    }
}

/// <summary>
/// Test schema builder for index inventory pass tests.
/// </summary>
internal static class IndexInventoryTestSchemaBuilder
{
    /// <summary>
    /// Build the standard pass list through index derivation.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughIndexDerivation()
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
        ];
    }

    /// <summary>
    /// Build project schema with references (School, Student, Enrollment).
    /// </summary>
    internal static JsonObject BuildSchoolWithReferenceProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema with abstract resource.
    /// </summary>
    internal static JsonObject BuildAbstractResourceProjectSchema()
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
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSubclassSchoolSchema() },
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
    /// Build core project schema for extension index testing.
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
    /// Build extension project schema for extension index testing.
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
    /// Build project schema with long resource name and references.
    /// </summary>
    internal static JsonObject BuildLongNameResourceProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["studentEducationOrganizationAssociations"] =
                    BuildStudentEducationOrganizationAssociationSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    private static JsonObject BuildEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.schoolReference.schoolId",
                "$.studentReference.studentUniqueId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("schoolId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildStudentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
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

    private static JsonObject BuildSubclassSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
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

    private static JsonObject BuildStudentEducationOrganizationAssociationSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "StudentEducationOrganizationAssociation",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.educationOrganizationReference.educationOrganizationId",
                "$.studentReference.studentUniqueId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
