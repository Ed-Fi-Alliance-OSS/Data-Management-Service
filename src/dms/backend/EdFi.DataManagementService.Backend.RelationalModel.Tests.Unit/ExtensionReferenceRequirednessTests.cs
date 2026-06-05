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
/// Test fixture for extension-scoped document reference requiredness.
/// </summary>
[TestFixture]
public class Given_ReferenceBindingRelationalModelSetPassTests_With_Extension_Reference_Requiredness
{
    private RelationalResourceModel _studentModel = default!;
    private DbTableModel _rootExtensionTable = default!;
    private DbTableModel _alignedExtensionTable = default!;
    private DbTableModel _extensionChildTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            ExtensionReferenceRequirednessSchemaBuilder.BuildCoreProjectSchema(),
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            ExtensionReferenceRequirednessSchemaBuilder.BuildExtensionProjectSchema(),
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new TransitiveIdentityMutabilityPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _studentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "Student"
            )
            .RelationalModel;

        _rootExtensionTable = RequireTable("sample", "StudentExtension");
        _alignedExtensionTable = RequireTable("sample", "StudentExtensionAddress");
        _extensionChildTable = RequireTable("sample", "StudentExtensionActivity");
    }

    /// <summary>
    /// It should make extension reference columns nullable when JSON schema omits the reference even if raw
    /// documentPathsMapping says required.
    /// </summary>
    [Test]
    public void It_should_make_optional_extension_references_nullable_from_json_schema_not_raw_isRequired()
    {
        AssertReferenceGroupNullability(_rootExtensionTable, "OptionalRootSponsorSchool", isNullable: true);
        AssertReferenceGroupNullability(
            _alignedExtensionTable,
            "OptionalAlignedSponsorSchool",
            isNullable: true
        );
        AssertReferenceGroupNullability(_extensionChildTable, "OptionalChildSponsorSchool", isNullable: true);
    }

    /// <summary>
    /// It should make extension reference columns non-nullable when JSON schema requires the reference inside
    /// the owning materialized extension row even if raw documentPathsMapping says optional.
    /// </summary>
    [Test]
    public void It_should_make_required_extension_references_non_nullable_inside_materialized_extension_rows()
    {
        AssertReferenceGroupNullability(_rootExtensionTable, "RequiredRootSponsorSchool", isNullable: false);
        AssertReferenceGroupNullability(
            _alignedExtensionTable,
            "RequiredAlignedSponsorSchool",
            isNullable: false
        );
        AssertReferenceGroupNullability(
            _extensionChildTable,
            "RequiredChildSponsorSchool",
            isNullable: false
        );
    }

    /// <summary>
    /// It should preserve all-or-none checks for optional extension reference groups.
    /// </summary>
    [Test]
    public void It_should_preserve_all_or_none_checks_for_optional_extension_reference_groups()
    {
        AssertAllOrNoneConstraint(_rootExtensionTable, "OptionalRootSponsorSchool");
        AssertAllOrNoneConstraint(_alignedExtensionTable, "OptionalAlignedSponsorSchool");
        AssertAllOrNoneConstraint(_extensionChildTable, "OptionalChildSponsorSchool");
    }

    /// <summary>
    /// It should preserve composite foreign keys for optional extension reference groups.
    /// </summary>
    [Test]
    public void It_should_preserve_composite_foreign_keys_for_optional_extension_reference_groups()
    {
        AssertCompositeForeignKey(_rootExtensionTable, "OptionalRootSponsorSchool");
        AssertCompositeForeignKey(_alignedExtensionTable, "OptionalAlignedSponsorSchool");
        AssertCompositeForeignKey(_extensionChildTable, "OptionalChildSponsorSchool");
    }

    /// <summary>
    /// It should not add choice-group constraints for nullable extension references.
    /// </summary>
    [Test]
    public void It_should_not_add_choice_group_constraints_for_nullable_extension_references()
    {
        var constraints = _studentModel
            .TablesInDependencyOrder.SelectMany(table => table.Constraints)
            .ToArray();

        constraints.Should().NotContain(constraint => constraint is TableConstraint.NullOrTrue);
        constraints
            .Select(GetConstraintName)
            .Should()
            .NotContain(name =>
                name.Contains("Choice", StringComparison.Ordinal)
                || name.Contains("AtMostOne", StringComparison.Ordinal)
                || name.Contains("ExactlyOne", StringComparison.Ordinal)
            );
    }

    private DbTableModel RequireTable(string schema, string tableName)
    {
        return _studentModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == schema && table.Table.Name == tableName
        );
    }

    private static void AssertReferenceGroupNullability(
        DbTableModel table,
        string referenceBaseName,
        bool isNullable
    )
    {
        var fkColumn = RequireColumn(table, $"{referenceBaseName}_DocumentId");
        var identityColumn = RequireColumn(table, $"{referenceBaseName}_SchoolId");

        fkColumn.IsNullable.Should().Be(isNullable);
        identityColumn.IsNullable.Should().Be(isNullable);
    }

    private static void AssertAllOrNoneConstraint(DbTableModel table, string referenceBaseName)
    {
        var constraint = table
            .Constraints.OfType<TableConstraint.AllOrNoneNullability>()
            .Single(constraint => constraint.FkColumn.Value == $"{referenceBaseName}_DocumentId");

        constraint
            .DependentColumns.Select(column => column.Value)
            .Should()
            .Equal($"{referenceBaseName}_SchoolId");
    }

    private static void AssertCompositeForeignKey(DbTableModel table, string referenceBaseName)
    {
        var constraint = table
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint =>
                constraint.Columns.Any(column => column.Value == $"{referenceBaseName}_DocumentId")
            );

        constraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal($"{referenceBaseName}_SchoolId", $"{referenceBaseName}_DocumentId");
        constraint.TargetTable.Schema.Value.Should().Be("edfi");
        constraint.TargetTable.Name.Should().Be("School");
        constraint.TargetColumns.Select(column => column.Value).Should().Equal("SchoolId", "DocumentId");
    }

    private static DbColumnModel RequireColumn(DbTableModel table, string columnName)
    {
        return table.Columns.Single(column => column.ColumnName.Value == columnName);
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            TableConstraint.NullOrTrue nullOrTrue => nullOrTrue.Name,
            _ => throw new InvalidOperationException(
                $"Unknown constraint type '{constraint.GetType().Name}'."
            ),
        };
    }
}

internal static class ExtensionReferenceRequirednessSchemaBuilder
{
    internal static JsonObject BuildCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["students"] = BuildStudentSchema(),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["students"] = BuildStudentExtensionSchema() },
        };
    }

    private static JsonObject BuildStudentSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["StudentUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.studentUniqueId",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["jsonSchemaForInsert"] = new JsonObject
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
                                ["street"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                            },
                        },
                    },
                },
                ["required"] = new JsonArray { "studentUniqueId" },
            },
        };
    }

    private static JsonObject BuildSchoolSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.schoolId",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                },
                ["required"] = new JsonArray { "schoolId" },
            },
        };
    }

    private static JsonObject BuildStudentExtensionSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["OptionalRootSponsorSchool"] = SchoolReferenceMapping(
                    "$._ext.sample.optionalRootSponsorReference.schoolId",
                    rawIsRequired: true
                ),
                ["RequiredRootSponsorSchool"] = SchoolReferenceMapping(
                    "$._ext.sample.requiredRootSponsorReference.schoolId",
                    rawIsRequired: false
                ),
                ["OptionalAlignedSponsorSchool"] = SchoolReferenceMapping(
                    "$._ext.sample.addresses[*]._ext.sample.optionalAlignedSponsorReference.schoolId",
                    rawIsRequired: true
                ),
                ["RequiredAlignedSponsorSchool"] = SchoolReferenceMapping(
                    "$._ext.sample.addresses[*]._ext.sample.requiredAlignedSponsorReference.schoolId",
                    rawIsRequired: false
                ),
                ["OptionalChildSponsorSchool"] = SchoolReferenceMapping(
                    "$._ext.sample.activities[*].optionalChildSponsorReference.schoolId",
                    rawIsRequired: true
                ),
                ["RequiredChildSponsorSchool"] = SchoolReferenceMapping(
                    "$._ext.sample.activities[*].requiredChildSponsorReference.schoolId",
                    rawIsRequired: false
                ),
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["jsonSchemaForInsert"] = BuildStudentExtensionJsonSchema(),
        };
    }

    private static JsonObject BuildStudentExtensionJsonSchema()
    {
        return new JsonObject
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
                                ["rootNote"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                                ["optionalRootSponsorReference"] = SchoolReferenceSchema(),
                                ["requiredRootSponsorReference"] = SchoolReferenceSchema(),
                                ["addresses"] = AlignedExtensionAddressesSchema(),
                                ["activities"] = ExtensionActivitiesSchema(),
                            },
                            ["required"] = new JsonArray { "requiredRootSponsorReference" },
                        },
                    },
                },
            },
        };
    }

    private static JsonObject AlignedExtensionAddressesSchema()
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
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
                                    ["alignedNote"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["maxLength"] = 50,
                                    },
                                    ["optionalAlignedSponsorReference"] = SchoolReferenceSchema(),
                                    ["requiredAlignedSponsorReference"] = SchoolReferenceSchema(),
                                },
                                ["required"] = new JsonArray { "requiredAlignedSponsorReference" },
                            },
                        },
                    },
                },
            },
        };
    }

    private static JsonObject ExtensionActivitiesSchema()
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["activityCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                    ["optionalChildSponsorReference"] = SchoolReferenceSchema(),
                    ["requiredChildSponsorReference"] = SchoolReferenceSchema(),
                },
                ["required"] = new JsonArray { "requiredChildSponsorReference" },
            },
        };
    }

    private static JsonObject SchoolReferenceSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
            },
        };
    }

    private static JsonObject SchoolReferenceMapping(string referenceJsonPath, bool rawIsRequired)
    {
        return new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["isRequired"] = rawIsRequired,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "School",
            ["referenceJsonPaths"] = new JsonArray
            {
                new JsonObject
                {
                    ["identityJsonPath"] = "$.schoolId",
                    ["referenceJsonPath"] = referenceJsonPath,
                },
            },
        };
    }
}
