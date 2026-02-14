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
/// Test fixture for reference-site key-unification behavior.
/// </summary>
[TestFixture]
public class Given_Key_Unification_For_Reference_Sites
{
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildReferenceUnificationProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(
            KeyUnificationPassTestSchemaBuilder.BuildPassesThroughKeyUnification()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _rootTable = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should create one canonical stored column per applied reference-site class.
    /// </summary>
    [Test]
    public void It_should_create_one_canonical_stored_column_per_applied_class()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        keyUnificationClass
            .MemberPathColumns.Select(column => column.Value)
            .Should()
            .Equal("School_SchoolId", "SecondarySchool_SchoolId");

        var canonicalColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(keyUnificationClass.CanonicalColumn)
        );
        canonicalColumn.SourceJsonPath.Should().BeNull();
        canonicalColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should gate unified reference-site aliases by each site's DocumentId presence column.
    /// </summary>
    [Test]
    public void It_should_gate_reference_aliases_by_reference_DocumentId_presence()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Single();

        var schoolIdAlias = _rootTable.Columns.Single(column => column.ColumnName.Value == "School_SchoolId");
        var secondarySchoolIdAlias = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "SecondarySchool_SchoolId"
        );
        var schoolStorage = schoolIdAlias.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject;
        var secondaryStorage = secondarySchoolIdAlias
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject;

        schoolStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        schoolStorage.PresenceColumn.Should().Be(new DbColumnName("School_DocumentId"));
        secondaryStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        secondaryStorage.PresenceColumn.Should().Be(new DbColumnName("SecondarySchool_DocumentId"));
        schoolIdAlias.SourceJsonPath!.Value.Canonical.Should().Be("$.schoolReference.schoolId");
        secondarySchoolIdAlias
            .SourceJsonPath!.Value.Canonical.Should()
            .Be("$.secondarySchoolReference.schoolId");
    }
}

/// <summary>
/// Test fixture for optional non-reference scalar key-unification behavior.
/// </summary>
[TestFixture]
public class Given_Key_Unification_For_Optional_NonReference_Scalars
{
    private DbTableModel _rootTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = KeyUnificationPassTestSchemaBuilder.BuildOptionalScalarUnificationProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(
            KeyUnificationPassTestSchemaBuilder.BuildPassesThroughKeyUnification()
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        _rootTable = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Example"
            )
            .RelationalModel.Root;
    }

    /// <summary>
    /// It should create synthetic presence flags for optional non-reference unified members.
    /// </summary>
    [Test]
    public void It_should_create_synthetic_presence_flags_for_optional_non_reference_members()
    {
        var fiscalYearPresence = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "FiscalYear_Present"
        );
        var localFiscalYearPresence = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "LocalFiscalYear_Present"
        );

        fiscalYearPresence.Kind.Should().Be(ColumnKind.Scalar);
        fiscalYearPresence.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Boolean));
        fiscalYearPresence.IsNullable.Should().BeTrue();
        fiscalYearPresence.SourceJsonPath.Should().BeNull();
        fiscalYearPresence.Storage.Should().BeOfType<ColumnStorage.Stored>();

        localFiscalYearPresence.Kind.Should().Be(ColumnKind.Scalar);
        localFiscalYearPresence.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Boolean));
        localFiscalYearPresence.IsNullable.Should().BeTrue();
        localFiscalYearPresence.SourceJsonPath.Should().BeNull();
        localFiscalYearPresence.Storage.Should().BeOfType<ColumnStorage.Stored>();
    }

    /// <summary>
    /// It should convert optional scalar members to presence-gated aliases of one canonical column.
    /// </summary>
    [Test]
    public void It_should_convert_optional_scalar_members_to_presence_gated_aliases()
    {
        var keyUnificationClass = _rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;
        var canonicalColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Equals(keyUnificationClass.CanonicalColumn)
        );
        var fiscalYear = _rootTable.Columns.Single(column => column.ColumnName.Value == "FiscalYear");
        var localFiscalYear = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "LocalFiscalYear"
        );
        var fiscalYearStorage = fiscalYear.Storage.Should().BeOfType<ColumnStorage.UnifiedAlias>().Subject;
        var localFiscalYearStorage = localFiscalYear
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject;

        keyUnificationClass
            .MemberPathColumns.Select(column => column.Value)
            .Should()
            .Equal("FiscalYear", "LocalFiscalYear");
        canonicalColumn.SourceJsonPath.Should().BeNull();
        canonicalColumn.Storage.Should().BeOfType<ColumnStorage.Stored>();
        fiscalYearStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        fiscalYearStorage.PresenceColumn.Should().Be(new DbColumnName("FiscalYear_Present"));
        localFiscalYearStorage.CanonicalColumn.Should().Be(keyUnificationClass.CanonicalColumn);
        localFiscalYearStorage.PresenceColumn.Should().Be(new DbColumnName("LocalFiscalYear_Present"));
        fiscalYear.SourceJsonPath!.Value.Canonical.Should().Be("$.fiscalYear");
        localFiscalYear.SourceJsonPath!.Value.Canonical.Should().Be("$.localFiscalYear");
    }
}

/// <summary>
/// Schema builders for key-unification pass tests.
/// </summary>
file static class KeyUnificationPassTestSchemaBuilder
{
    /// <summary>
    /// Build pass list up to key-unification.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughKeyUnification()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new CanonicalizeOrderingPass(),
        ];
    }

    /// <summary>
    /// Build project schema for reference-site unification.
    /// </summary>
    internal static JsonObject BuildReferenceUnificationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentReferenceUnificationSchema(),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema for optional non-reference scalar unification.
    /// </summary>
    internal static JsonObject BuildOptionalScalarUnificationProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["examples"] = BuildOptionalScalarResourceSchema() },
        };
    }

    /// <summary>
    /// Build enrollment schema with two optional references that share one identity value.
    /// </summary>
    private static JsonObject BuildEnrollmentReferenceUnificationSchema()
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
                ["secondarySchoolReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
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
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
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
                ["SecondarySchool"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.secondarySchoolReference.schoolId",
                        },
                    },
                },
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.schoolReference.schoolId",
                    ["targetJsonPath"] = "$.secondarySchoolReference.schoolId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build canonical target school schema.
    /// </summary>
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
            ["equalityConstraints"] = new JsonArray(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build optional scalar schema with one equality constraint.
    /// </summary>
    private static JsonObject BuildOptionalScalarResourceSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["fiscalYear"] = new JsonObject { ["type"] = "integer" },
                ["localFiscalYear"] = new JsonObject { ["type"] = "integer" },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Example",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.fiscalYear",
                    ["targetJsonPath"] = "$.localFiscalYear",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
