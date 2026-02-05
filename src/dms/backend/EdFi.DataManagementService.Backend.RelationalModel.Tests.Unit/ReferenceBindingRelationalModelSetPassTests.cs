// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for reference binding.
/// </summary>
[TestFixture]
public class Given_Reference_Binding
{
    private RelationalResourceModel _studentModel = default!;
    private DbTableModel _rootTable = default!;
    private DbTableModel _periodTable = default!;
    private DbTableModel _extensionRootTable = default!;
    private DocumentReferenceBinding _schoolBinding = default!;
    private DocumentReferenceBinding _calendarBinding = default!;
    private DocumentReferenceBinding _personBinding = default!;
    private DocumentReferenceBinding _programBinding = default!;
    private DocumentReferenceBinding _extensionBinding = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ReferenceBindingTestSchemaBuilder.BuildCoreProjectSchema();
        var extensionProjectSchema = ReferenceBindingTestSchemaBuilder.BuildExtensionProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ExtensionTableDerivationRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _studentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ProjectName == "Ed-Fi"
                && model.ResourceKey.Resource.ResourceName == "Student"
            )
            .RelationalModel;

        _rootTable = _studentModel.TablesInDependencyOrder.Single(table => table.JsonScope.Canonical == "$");
        _periodTable = _studentModel.TablesInDependencyOrder.Single(table =>
            table.JsonScope.Canonical == "$.addresses[*].periods[*]"
        );
        _extensionRootTable = _studentModel.TablesInDependencyOrder.Single(table =>
            table.Table.Schema.Value == "sample" && table.Table.Name == "StudentExtension"
        );

        _schoolBinding = _studentModel.DocumentReferenceBindings.Single(binding =>
            binding.ReferenceObjectPath.Canonical == "$.schoolReference"
        );
        _calendarBinding = _studentModel.DocumentReferenceBindings.Single(binding =>
            binding.ReferenceObjectPath.Canonical == "$.addresses[*].periods[*].calendarReference"
        );
        _personBinding = _studentModel.DocumentReferenceBindings.Single(binding =>
            binding.ReferenceObjectPath.Canonical == "$.personReference.name"
        );
        _programBinding = _studentModel.DocumentReferenceBindings.Single(binding =>
            binding.ReferenceObjectPath.Canonical == "$.programReference"
        );
        _extensionBinding = _studentModel.DocumentReferenceBindings.Single(binding =>
            binding.ReferenceObjectPath.Canonical == "$._ext.sample.sponsorReference"
        );
    }

    /// <summary>
    /// It should bind nested references to child tables.
    /// </summary>
    [Test]
    public void It_should_bind_nested_references_to_child_tables()
    {
        _calendarBinding.Table.Should().Be(_periodTable.Table);
        _calendarBinding.FkColumn.Value.Should().Be("Calendar_DocumentId");

        _periodTable.Columns.Should().Contain(column => column.ColumnName.Value == "Calendar_DocumentId");
    }

    /// <summary>
    /// It should classify identity component references.
    /// </summary>
    [Test]
    public void It_should_classify_identity_component_references()
    {
        _schoolBinding.IsIdentityComponent.Should().BeTrue();
        _calendarBinding.IsIdentityComponent.Should().BeFalse();
        _personBinding.IsIdentityComponent.Should().BeFalse();
        _programBinding.IsIdentityComponent.Should().BeFalse();
    }

    /// <summary>
    /// It should use expanded identity naming for nested paths.
    /// </summary>
    [Test]
    public void It_should_use_expanded_identity_naming_for_nested_paths()
    {
        var personColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "Person_NameFirstName"
        );

        personColumn.SourceJsonPath.Should().NotBeNull();
        personColumn.SourceJsonPath!.Value.Canonical.Should().Be("$.personReference.name.firstName");
        _personBinding.IdentityBindings.Single().Column.Value.Should().Be("Person_NameFirstName");
    }

    /// <summary>
    /// It should bind descriptor identity columns inside reference objects.
    /// </summary>
    [Test]
    public void It_should_bind_descriptor_identity_columns_inside_reference_objects()
    {
        var descriptorColumn = _rootTable.Columns.Single(column =>
            column.ColumnName.Value == "Program_ProgramTypeDescriptor_DescriptorId"
        );

        descriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);

        var descriptorFk = _rootTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint =>
                constraint.Columns.Single().Value == "Program_ProgramTypeDescriptor_DescriptorId"
            );

        descriptorFk.TargetTable.Schema.Value.Should().Be("dms");
        descriptorFk.TargetTable.Name.Should().Be("Descriptor");

        var descriptorEdge = _studentModel.DescriptorEdgeSources.Single(edge =>
            edge.FkColumn.Value == "Program_ProgramTypeDescriptor_DescriptorId"
        );

        descriptorEdge.DescriptorValuePath.Canonical.Should().Be("$.programReference.programTypeDescriptor");
        descriptorEdge.DescriptorResource.ResourceName.Should().Be("ProgramTypeDescriptor");
    }

    /// <summary>
    /// It should bind extension references to extension tables.
    /// </summary>
    [Test]
    public void It_should_bind_extension_references_to_extension_tables()
    {
        _extensionBinding.Table.Schema.Value.Should().Be("sample");
        _extensionBinding.Table.Name.Should().Be("StudentExtension");
        _extensionBinding.FkColumn.Value.Should().Be("SponsorSchool_DocumentId");

        _extensionRootTable
            .Columns.Should()
            .Contain(column => column.ColumnName.Value == "SponsorSchool_DocumentId");
    }
}

/// <summary>
/// Test fixture for inside-reference identity overrides during reference binding.
/// </summary>
[TestFixture]
public class Given_Reference_Binding_With_Identity_NameOverride
{
    private RelationalResourceModel _model = default!;
    private DocumentReferenceBinding _schoolBinding = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = BuildProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _model = result
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource.ResourceName == "Student"
            )
            .RelationalModel;

        _schoolBinding = _model.DocumentReferenceBindings.Single(binding =>
            binding.ReferenceObjectPath.Canonical == "$.schoolReference"
        );
    }

    /// <summary>
    /// It should apply identity overrides to propagated reference identity columns.
    /// </summary>
    [Test]
    public void It_should_apply_identity_overrides_to_reference_identity_columns()
    {
        _schoolBinding.FkColumn.Value.Should().Be("School_DocumentId");
        _schoolBinding.IdentityBindings.Single().Column.Value.Should().Be("School_CampusId");

        _model.Root.Columns.Should().Contain(column => column.ColumnName.Value == "School_CampusId");
    }

    private static JsonObject BuildProjectSchema()
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

    private static JsonObject BuildStudentSchema()
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
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
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
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
            ["relational"] = new JsonObject
            {
                ["nameOverrides"] = new JsonObject { ["$.schoolReference.schoolId"] = "campusId" },
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
                ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
                ["required"] = new JsonArray { "schoolId" },
            },
        };
    }
}

/// <summary>
/// Test fixture for reference binding when descriptor path is missing.
/// </summary>
[TestFixture]
public class Given_Reference_Binding_When_Descriptor_Path_Is_Missing
{
    private Action _action = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = ReferenceBindingMissingDescriptorPathSchemaBuilder.BuildProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
            }
        );

        _action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast when descriptor identity path is missing from map.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_descriptor_identity_path_is_missing_from_map()
    {
        _action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*$.programTypeDescriptor*Ed-Fi:Program*descriptor path map*");
    }
}

/// <summary>
/// Test type reference binding test schema builder.
/// </summary>
internal static class ReferenceBindingTestSchemaBuilder
{
    /// <summary>
    /// Build core project schema.
    /// </summary>
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
                ["persons"] = BuildPersonSchema(),
                ["programs"] = BuildProgramSchema(),
                ["calendars"] = BuildCalendarSchema(),
                ["programTypeDescriptors"] = BuildProgramTypeDescriptorSchema(),
            },
        };
    }

    /// <summary>
    /// Build extension project schema.
    /// </summary>
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

    /// <summary>
    /// Build student schema.
    /// </summary>
    private static JsonObject BuildStudentSchema()
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
                ["personReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["firstName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                            },
                        },
                    },
                },
                ["programReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["programTypeDescriptor"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 300,
                        },
                    },
                },
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["periods"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["calendarReference"] = new JsonObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JsonObject
                                            {
                                                ["calendarCode"] = new JsonObject
                                                {
                                                    ["type"] = "string",
                                                    ["maxLength"] = 20,
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
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
                ["Person"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Person",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.name.firstName",
                            ["referenceJsonPath"] = "$.personReference.name.firstName",
                        },
                    },
                },
                ["Program"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Program",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.programTypeDescriptor",
                            ["referenceJsonPath"] = "$.programReference.programTypeDescriptor",
                        },
                    },
                },
                ["Calendar"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Calendar",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.calendarCode",
                            ["referenceJsonPath"] =
                                "$.addresses[*].periods[*].calendarReference.calendarCode",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build student extension schema.
    /// </summary>
    private static JsonObject BuildStudentExtensionSchema()
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
                                ["sponsorReference"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["schoolId"] = new JsonObject { ["type"] = "integer" },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["SponsorSchool"] = new JsonObject
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
                            ["referenceJsonPath"] = "$._ext.sample.sponsorReference.schoolId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build school schema.
    /// </summary>
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
                ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
                ["required"] = new JsonArray("schoolId"),
            },
        };
    }

    /// <summary>
    /// Build person schema.
    /// </summary>
    private static JsonObject BuildPersonSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Person",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.name.firstName" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["Name.FirstName"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.name.firstName",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["firstName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                        },
                        ["required"] = new JsonArray("firstName"),
                    },
                },
                ["required"] = new JsonArray("name"),
            },
        };
    }

    /// <summary>
    /// Build program schema.
    /// </summary>
    private static JsonObject BuildProgramSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Program",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.programTypeDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["ProgramTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "ProgramTypeDescriptor",
                    ["path"] = "$.programTypeDescriptor",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["programTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 300 },
                },
                ["required"] = new JsonArray("programTypeDescriptor"),
            },
        };
    }

    /// <summary>
    /// Build calendar schema.
    /// </summary>
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
                ["required"] = new JsonArray("calendarCode"),
            },
        };
    }

    /// <summary>
    /// Build program type descriptor schema.
    /// </summary>
    private static JsonObject BuildProgramTypeDescriptorSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ProgramTypeDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                },
            },
        };
    }
}

/// <summary>
/// Test type reference binding missing descriptor path schema builder.
/// </summary>
internal static class ReferenceBindingMissingDescriptorPathSchemaBuilder
{
    /// <summary>
    /// Build project schema.
    /// </summary>
    internal static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["students"] = BuildStudentSchema(),
                ["programs"] = BuildProgramSchema(),
            },
        };
    }

    /// <summary>
    /// Build student schema.
    /// </summary>
    private static JsonObject BuildStudentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["programReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["programTypeDescriptor"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 300,
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["Program"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Program",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.programTypeDescriptor",
                            ["referenceJsonPath"] = "$.programReference.programTypeDescriptor",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build program schema.
    /// </summary>
    private static JsonObject BuildProgramSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Program",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.programTypeDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["ProgramTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.programTypeDescriptor",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["programTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 300 },
                },
                ["required"] = new JsonArray("programTypeDescriptor"),
            },
        };
    }
}
