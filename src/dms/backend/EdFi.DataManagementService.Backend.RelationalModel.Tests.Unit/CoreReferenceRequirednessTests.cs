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
/// Test fixture for core document reference requiredness.
/// </summary>
[TestFixture]
public class Given_ReferenceBindingRelationalModelSetPassTests_With_Core_Reference_Requiredness
{
    private RelationalResourceModel _studentModel = default!;
    private DbTableModel _rootTable = default!;
    private DbTableModel _addressTable = default!;
    private DbTableModel _periodTable = default!;
    private RelationalModelBuilderContext _extractInputsContext = default!;
    private JsonObject _studentSchema = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = CoreReferenceRequirednessSchemaBuilder.BuildCoreProjectSchema();
        var resourceSchemas = RequireObject(coreProjectSchema["resourceSchemas"], "resourceSchemas");
        _studentSchema = RequireObject(resourceSchemas["students"], "resourceSchemas.students");

        var apiSchemaRoot = CoreReferenceRequirednessSchemaBuilder.CreateApiSchemaRoot(coreProjectSchema);
        _extractInputsContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "students",
        };
        new ExtractInputsStep().Execute(_extractInputsContext);

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _studentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "Student"
            )
            .RelationalModel;

        _rootTable = RequireTable("$");
        _addressTable = RequireTable("$.addresses[*]");
        _periodTable = RequireTable("$.addresses[*].periods[*]");
    }

    /// <summary>
    /// It should make optional choice-like root references nullable from JSON schema requiredness.
    /// </summary>
    [Test]
    public void It_should_make_optional_choice_like_root_reference_nullable_from_json_schema_not_raw_isRequired()
    {
        AssertReferenceGroupNullability(_rootTable, "ChoiceSchool", isNullable: true);
    }

    /// <summary>
    /// It should make required root references non-nullable from JSON schema requiredness.
    /// </summary>
    [Test]
    public void It_should_make_required_root_reference_non_nullable_from_json_schema_not_raw_isRequired()
    {
        AssertReferenceGroupNullability(_rootTable, "RequiredSchool", isNullable: false);
    }

    /// <summary>
    /// It should make x-nullable reference objects optional even when the root required array includes them.
    /// </summary>
    [Test]
    public void It_should_make_x_nullable_reference_objects_optional_even_when_required()
    {
        AssertReferenceGroupNullability(_rootTable, "NullableSchool", isNullable: true);
    }

    /// <summary>
    /// It should ignore collection row-presence ancestors inside materialized child and nested rows.
    /// </summary>
    [Test]
    public void It_should_ignore_collection_row_presence_ancestors_inside_materialized_rows()
    {
        AssertReferenceGroupNullability(_addressTable, "RequiredAddressSchool", isNullable: false);
        AssertReferenceGroupNullability(
            _periodTable,
            "RequiredPeriodCalendar",
            isNullable: false,
            identityPartName: "CalendarCode"
        );
    }

    /// <summary>
    /// It should keep optional child and nested references nullable from the owning-scope schema.
    /// </summary>
    [Test]
    public void It_should_keep_optional_child_and_nested_references_nullable()
    {
        AssertReferenceGroupNullability(_addressTable, "OptionalAddressSchool", isNullable: true);
        AssertReferenceGroupNullability(
            _periodTable,
            "OptionalPeriodCalendar",
            isNullable: true,
            identityPartName: "CalendarCode"
        );
    }

    /// <summary>
    /// It should preserve raw document path requiredness while storing derived relational mapping requiredness.
    /// </summary>
    [Test]
    public void It_should_preserve_raw_document_path_requiredness_while_storing_derived_mapping_requiredness()
    {
        var rawMappings = RequireObject(_studentSchema["documentPathsMapping"], "documentPathsMapping");

        RequireRawIsRequired(rawMappings, "ChoiceSchool").Should().BeTrue();
        RequireRawIsRequired(rawMappings, "RequiredSchool").Should().BeFalse();
        RequireRawIsRequired(rawMappings, "NullableSchool").Should().BeTrue();

        RequireExtractedReferenceMapping("ChoiceSchool").IsRequired.Should().BeFalse();
        RequireExtractedReferenceMapping("RequiredSchool").IsRequired.Should().BeTrue();
        RequireExtractedReferenceMapping("NullableSchool").IsRequired.Should().BeFalse();
    }

    /// <summary>
    /// It should fail fast when an identity path depends on an optional reference group.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_identity_path_depends_on_optional_reference_group()
    {
        var exception = CoreReferenceRequirednessSchemaBuilder.CaptureExtractInputsException(
            CoreReferenceRequirednessSchemaBuilder.BuildStudentWithOptionalIdentityReferenceSchema()
        );

        exception.Should().BeOfType<InvalidOperationException>();
        exception.Message.Should().Contain("Ed-Fi:Student");
        exception.Message.Should().Contain("$.optionalIdentitySchoolReference");
        exception.Message.Should().Contain("$.optionalIdentitySchoolReference.schoolId");
    }

    private DbTableModel RequireTable(string jsonScope)
    {
        return _studentModel.TablesInDependencyOrder.Single(table => table.JsonScope.Canonical == jsonScope);
    }

    private DocumentReferenceMapping RequireExtractedReferenceMapping(string mappingKey)
    {
        return _extractInputsContext.DocumentReferenceMappings.Single(mapping =>
            mapping.MappingKey == mappingKey
        );
    }

    private static void AssertReferenceGroupNullability(
        DbTableModel table,
        string referenceBaseName,
        bool isNullable,
        string identityPartName = "SchoolId"
    )
    {
        var fkColumn = RequireColumn(table, $"{referenceBaseName}_DocumentId");
        var identityColumn = RequireColumn(table, $"{referenceBaseName}_{identityPartName}");

        fkColumn.IsNullable.Should().Be(isNullable);
        identityColumn.IsNullable.Should().Be(isNullable);
    }

    private static DbColumnModel RequireColumn(DbTableModel table, string columnName)
    {
        return table.Columns.Single(column => column.ColumnName.Value == columnName);
    }

    private static bool RequireRawIsRequired(JsonObject documentPathsMapping, string mappingKey)
    {
        var mapping = RequireObject(documentPathsMapping[mappingKey], $"documentPathsMapping.{mappingKey}");
        var rawIsRequired = mapping["isRequired"];

        return rawIsRequired?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                $"Expected documentPathsMapping.{mappingKey}.isRequired to be present."
            );
    }

    private static JsonObject RequireObject(JsonNode? node, string path)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException($"Expected {path} to be present."),
            _ => throw new InvalidOperationException($"Expected {path} to be an object."),
        };
    }
}

internal static class CoreReferenceRequirednessSchemaBuilder
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
                ["calendars"] = BuildCalendarSchema(),
            },
        };
    }

    internal static JsonObject CreateApiSchemaRoot(JsonObject coreProjectSchema)
    {
        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = coreProjectSchema };
    }

    internal static JsonObject BuildStudentWithOptionalIdentityReferenceSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.optionalIdentitySchoolReference.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["OptionalIdentitySchool"] = SchoolReferenceMapping(
                    "$.optionalIdentitySchoolReference.schoolId",
                    rawIsRequired: true
                ),
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["optionalIdentitySchoolReference"] = SchoolReferenceSchema(),
                },
            },
        };
    }

    internal static Exception CaptureExtractInputsException(JsonObject studentSchema)
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["students"] = studentSchema },
        };
        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = CreateApiSchemaRoot(projectSchema),
            ResourceEndpointName = "students",
        };

        try
        {
            new ExtractInputsStep().Execute(context);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected ExtractInputsStep to fail.");
    }

    private static JsonObject BuildStudentSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
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
                ["ChoiceSchool"] = SchoolReferenceMapping(
                    "$.choiceSchoolReference.schoolId",
                    rawIsRequired: true
                ),
                ["RequiredSchool"] = SchoolReferenceMapping(
                    "$.requiredSchoolReference.schoolId",
                    rawIsRequired: false
                ),
                ["NullableSchool"] = SchoolReferenceMapping(
                    "$.nullableSchoolReference.schoolId",
                    rawIsRequired: true
                ),
                ["OptionalAddressSchool"] = SchoolReferenceMapping(
                    "$.addresses[*].optionalSchoolReference.schoolId",
                    rawIsRequired: true
                ),
                ["RequiredAddressSchool"] = SchoolReferenceMapping(
                    "$.addresses[*].requiredSchoolReference.schoolId",
                    rawIsRequired: false
                ),
                ["OptionalPeriodCalendar"] = CalendarReferenceMapping(
                    "$.addresses[*].periods[*].optionalCalendarReference.calendarCode",
                    rawIsRequired: true
                ),
                ["RequiredPeriodCalendar"] = CalendarReferenceMapping(
                    "$.addresses[*].periods[*].requiredCalendarReference.calendarCode",
                    rawIsRequired: false
                ),
            },
            ["jsonSchemaForInsert"] = BuildStudentJsonSchema(),
        };
    }

    private static JsonObject BuildStudentJsonSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                ["choiceSchoolReference"] = SchoolReferenceSchema(),
                ["requiredSchoolReference"] = SchoolReferenceSchema(),
                ["nullableSchoolReference"] = NullableSchoolReferenceSchema(),
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["street"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                            ["optionalSchoolReference"] = SchoolReferenceSchema(),
                            ["requiredSchoolReference"] = SchoolReferenceSchema(),
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["periodName"] = new JsonObject
                                        {
                                            ["type"] = "string",
                                            ["maxLength"] = 30,
                                        },
                                        ["optionalCalendarReference"] = CalendarReferenceSchema(),
                                        ["requiredCalendarReference"] = CalendarReferenceSchema(),
                                    },
                                    ["required"] = new JsonArray { "requiredCalendarReference" },
                                },
                            },
                        },
                        ["required"] = new JsonArray { "requiredSchoolReference" },
                    },
                },
            },
            ["required"] = new JsonArray
            {
                "studentUniqueId",
                "requiredSchoolReference",
                "nullableSchoolReference",
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
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.schoolId",
                },
            },
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

    private static JsonObject BuildCalendarSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Calendar",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.calendarCode" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["CalendarCode"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.calendarCode",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["calendarCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                },
                ["required"] = new JsonArray { "calendarCode" },
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
            ["required"] = new JsonArray { "schoolId" },
        };
    }

    private static JsonObject NullableSchoolReferenceSchema()
    {
        var schema = SchoolReferenceSchema();
        schema["x-nullable"] = true;

        return schema;
    }

    private static JsonObject CalendarReferenceSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["calendarCode"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
            ["required"] = new JsonArray { "calendarCode" },
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

    private static JsonObject CalendarReferenceMapping(string referenceJsonPath, bool rawIsRequired)
    {
        return new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["isRequired"] = rawIsRequired,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "Calendar",
            ["referenceJsonPaths"] = new JsonArray
            {
                new JsonObject
                {
                    ["identityJsonPath"] = "$.calendarCode",
                    ["referenceJsonPath"] = referenceJsonPath,
                },
            },
        };
    }
}
