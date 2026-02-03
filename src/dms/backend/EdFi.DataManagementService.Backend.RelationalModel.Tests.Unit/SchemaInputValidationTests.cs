// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Document_Reference_Without_ReferenceJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_missing_reference_json_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("referenceJsonPaths");
        _exception.Message.Should().Contain("School");
    }
}

[TestFixture]
public class Given_A_Document_Reference_With_Inconsistent_ReferenceJsonPaths_Prefix
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
                ["referenceJsonPaths"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolId",
                        ["referenceJsonPath"] = "$.schoolReference.schoolId",
                    },
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.districtId",
                        ["referenceJsonPath"] = "$.districtReference.districtId",
                    },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_inconsistent_prefix()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("inconsistent referenceJsonPaths prefix");
        _exception.Message.Should().Contain("School");
    }
}

[TestFixture]
public class Given_A_Document_Reference_With_Duplicate_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
                ["referenceJsonPaths"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolId",
                        ["referenceJsonPath"] = "$.schoolReference.schoolId",
                    },
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolId",
                        ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                    },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_duplicate_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate identityJsonPath");
        _exception.Message.Should().Contain("$.schoolId");
    }
}

[TestFixture]
public class Given_A_Document_Reference_Marked_As_Identity_With_Partial_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var identityJsonPaths = new JsonArray { "$.schoolReference.schoolId" };

        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = true,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
                ["referenceJsonPaths"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolId",
                        ["referenceJsonPath"] = "$.schoolReference.schoolId",
                    },
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolYear",
                        ["referenceJsonPath"] = "$.schoolReference.schoolYear",
                    },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: identityJsonPaths,
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_missing_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("isPartOfIdentity");
        _exception.Message.Should().Contain("missing reference path");
        _exception.Message.Should().Contain("$.schoolReference");
    }
}

[TestFixture]
public class Given_A_Document_Reference_Marked_As_Identity_Without_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = true,
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
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_missing_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("isPartOfIdentity");
        _exception.Message.Should().Contain("does not include path");
        _exception.Message.Should().Contain("$.schoolReference.schoolId");
    }
}

[TestFixture]
public class Given_A_Document_Reference_Not_Marked_As_Identity_With_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var identityJsonPaths = new JsonArray { "$.schoolReference.schoolId" };

        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
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
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: identityJsonPaths,
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_identity_paths_present()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("isPartOfIdentity");
        _exception.Message.Should().Contain("includes it");
    }
}

[TestFixture]
public class Given_An_Identity_Reference_That_Is_Not_Required
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var identityJsonPaths = new JsonArray { "$.schoolReference.schoolId" };

        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = true,
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
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: identityJsonPaths,
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_optional_identity_reference()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("isPartOfIdentity");
        _exception.Message.Should().Contain("isRequired");
        _exception.Message.Should().Contain("School");
    }
}

[TestFixture]
public class Given_A_Scalar_Marked_As_Identity_Without_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["SchoolId"] = new JsonObject
            {
                ["isReference"] = false,
                ["isPartOfIdentity"] = true,
                ["path"] = "$.schoolId",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_missing_identity_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("isPartOfIdentity");
        _exception.Message.Should().Contain("does not include path");
        _exception.Message.Should().Contain("$.schoolId");
    }
}

[TestFixture]
public class Given_A_Scalar_Not_Marked_As_Identity_With_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["SchoolId"] = new JsonObject
            {
                ["isReference"] = false,
                ["isPartOfIdentity"] = false,
                ["path"] = "$.schoolId",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray { "$.schoolId" },
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_identity_paths_present()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("not marked as isPartOfIdentity");
        _exception.Message.Should().Contain("includes it");
    }
}

[TestFixture]
public class Given_A_Descriptor_Marked_As_Identity_Without_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["SchoolTypeDescriptor"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = true,
                ["isPartOfIdentity"] = true,
                ["path"] = "$.schoolTypeDescriptor",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_missing_identity_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("isPartOfIdentity");
        _exception.Message.Should().Contain("does not include path");
        _exception.Message.Should().Contain("$.schoolTypeDescriptor");
    }
}

[TestFixture]
public class Given_A_Descriptor_Not_Marked_As_Identity_With_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["SchoolTypeDescriptor"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = true,
                ["isPartOfIdentity"] = false,
                ["path"] = "$.schoolTypeDescriptor",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray { "$.schoolTypeDescriptor" },
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_identity_paths_present()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("not marked as isPartOfIdentity");
        _exception.Message.Should().Contain("includes it");
    }
}

[TestFixture]
public class Given_IdentityJsonPaths_Not_Mapped_In_DocumentPathsMapping
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var identityJsonPaths = new JsonArray { "$.missingId" };
        var documentPathsMapping = new JsonObject
        {
            ["ActualId"] = new JsonObject
            {
                ["isReference"] = false,
                ["isPartOfIdentity"] = false,
                ["path"] = "$.actualId",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: identityJsonPaths,
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_unmapped_identity_json_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("identityJsonPaths");
        _exception.Message.Should().Contain("$.missingId");
    }
}

[TestFixture]
public class Given_Duplicate_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var identityJsonPaths = new JsonArray { "$.schoolId", "$.schoolId" };
        var documentPathsMapping = new JsonObject
        {
            ["SchoolId"] = new JsonObject
            {
                ["isReference"] = false,
                ["isPartOfIdentity"] = false,
                ["path"] = "$.schoolId",
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: identityJsonPaths,
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    [Test]
    public void It_should_fail_with_duplicate_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate");
        _exception.Message.Should().Contain("$.schoolId");
        _exception.Message.Should().Contain("Ed-Fi:Section");
    }
}

[TestFixture]
public class Given_Nested_ArrayUniquenessConstraint_Without_BasePath
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var arrayUniquenessConstraints = new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray { "$.items[*].id" },
                ["nestedConstraints"] = new JsonArray
                {
                    new JsonObject { ["paths"] = new JsonArray { "$.nestedId" } },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: new JsonObject(),
            jsonSchemaForInsert: new JsonObject(),
            arrayUniquenessConstraints: arrayUniquenessConstraints
        );
    }

    [Test]
    public void It_should_fail_with_missing_base_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("nestedConstraints");
        _exception.Message.Should().Contain("basePath");
    }
}

[TestFixture]
public class Given_ArrayUniquenessConstraint_With_Partial_Reference_Identity_Paths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
                ["referenceJsonPaths"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolId",
                        ["referenceJsonPath"] = "$.schools[*].schoolReference.schoolId",
                    },
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolYear",
                        ["referenceJsonPath"] = "$.schools[*].schoolReference.schoolYear",
                    },
                },
            },
        };

        var arrayUniquenessConstraints = new JsonArray
        {
            new JsonObject { ["paths"] = new JsonArray { "$.schools[*].schoolReference.schoolId" } },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject(),
            arrayUniquenessConstraints: arrayUniquenessConstraints
        );
    }

    [Test]
    public void It_should_fail_with_missing_reference_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints scope");
        _exception.Message.Should().Contain("$.schools[*].schoolReference");
        _exception.Message.Should().Contain("$.schools[*].schoolReference.schoolYear");
    }
}

[TestFixture]
public class Given_Nested_ArrayUniquenessConstraint_With_Partial_Reference_Identity_Paths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
                ["referenceJsonPaths"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolId",
                        ["referenceJsonPath"] = "$.schools[*].assignments[*].schoolReference.schoolId",
                    },
                    new JsonObject
                    {
                        ["identityJsonPath"] = "$.schoolYear",
                        ["referenceJsonPath"] = "$.schools[*].assignments[*].schoolReference.schoolYear",
                    },
                },
            },
        };

        var arrayUniquenessConstraints = new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray { "$.schools[*].id" },
                ["nestedConstraints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["basePath"] = "$.schools[*].assignments[*]",
                        ["paths"] = new JsonArray { "$.schoolReference.schoolId" },
                    },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject(),
            arrayUniquenessConstraints: arrayUniquenessConstraints
        );
    }

    [Test]
    public void It_should_fail_with_missing_nested_reference_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints scope");
        _exception.Message.Should().Contain("basePath '$.schools[*].assignments[*]'");
        _exception.Message.Should().Contain("$.schools[*].assignments[*].schoolReference");
        _exception.Message.Should().Contain("$.schools[*].assignments[*].schoolReference.schoolYear");
    }
}

[TestFixture]
public class Given_ArrayUniquenessConstraint_With_Unknown_Paths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var arrayUniquenessConstraints = new JsonArray
        {
            new JsonObject { ["paths"] = new JsonArray { "$.items[*].missing" } },
        };

        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
                    },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureValidationPipelineException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: new JsonObject(),
            jsonSchemaForInsert: jsonSchemaForInsert,
            arrayUniquenessConstraints: arrayUniquenessConstraints
        );
    }

    [Test]
    public void It_should_fail_with_unknown_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints paths");
        _exception.Message.Should().Contain("$.items[*].missing");
    }
}

[TestFixture]
public class Given_ArrayUniquenessConstraint_With_Unknown_BasePath
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var arrayUniquenessConstraints = new JsonArray
        {
            new JsonObject
            {
                ["basePath"] = "$.missing[*]",
                ["paths"] = new JsonArray { "$.id" },
            },
        };

        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
                    },
                },
            },
        };

        _exception = SchemaInputValidationHelpers.CaptureValidationPipelineException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: new JsonObject(),
            jsonSchemaForInsert: jsonSchemaForInsert,
            arrayUniquenessConstraints: arrayUniquenessConstraints
        );
    }

    [Test]
    public void It_should_fail_with_unknown_base_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints basePath");
        _exception.Message.Should().Contain("$.missing[*]");
    }
}

[TestFixture]
public class Given_Reference_IdentityJsonPath_Not_In_Target_IdentityJsonPaths
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var resourceSchemas = new JsonObject
        {
            ["sections"] = new JsonObject
            {
                ["resourceName"] = "Section",
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
                        ["isPartOfIdentity"] = false,
                        ["projectName"] = "Ed-Fi",
                        ["resourceName"] = "School",
                        ["referenceJsonPaths"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["identityJsonPath"] = "$.schoolTypeDescriptor",
                                ["referenceJsonPath"] = "$.schoolReference.schoolTypeDescriptor",
                            },
                        },
                    },
                },
                ["jsonSchemaForInsert"] = new JsonObject(),
            },
            ["schools"] = new JsonObject
            {
                ["resourceName"] = "School",
                ["isDescriptor"] = false,
                ["isResourceExtension"] = false,
                ["allowIdentityUpdates"] = false,
                ["arrayUniquenessConstraints"] = new JsonArray(),
                ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                ["documentPathsMapping"] = new JsonObject(),
                ["jsonSchemaForInsert"] = new JsonObject(),
            },
        };

        var projectSchema = new JsonObject { ["resourceSchemas"] = resourceSchemas };
        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "Section"),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "School"),
        };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);
        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_missing_target_identity_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("identityJsonPath");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

[TestFixture]
public class Given_A_Relational_NameOverride_With_Invalid_JsonPath
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var relational = new JsonObject
        {
            ["nameOverrides"] = new JsonObject { ["$.schoolReference..schoolId"] = "School" },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: new JsonObject(),
            jsonSchemaForInsert: new JsonObject(),
            relational: relational
        );
    }

    [Test]
    public void It_should_fail_with_invalid_json_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("relational.nameOverrides");
        _exception.Message.Should().Contain("$.schoolReference..schoolId");
        _exception.Message.Should().Contain("Ed-Fi:Section");
    }
}

[TestFixture]
public class Given_A_Relational_NameOverride_For_NonReference_Path
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
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
        };

        var relational = new JsonObject { ["nameOverrides"] = new JsonObject { ["$.schoolId"] = "School" } };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject(),
            relational: relational
        );
    }

    [Test]
    public void It_should_fail_with_unsupported_override_key()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("unsupported");
        _exception.Message.Should().Contain("$.schoolId");
        _exception.Message.Should().Contain("Ed-Fi:Section");
    }
}

[TestFixture]
public class Given_A_Relational_NameOverride_For_A_Reference_Path
{
    private RelationalModelBuilderContext? _context;

    [SetUp]
    public void Setup()
    {
        var documentPathsMapping = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isPartOfIdentity"] = false,
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
        };

        var relational = new JsonObject
        {
            ["nameOverrides"] = new JsonObject { ["$.schoolReference"] = "SchoolReferenceOverride" },
        };

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject(),
            relational: relational
        );
    }

    [Test]
    public void It_should_apply_the_override()
    {
        _context.Should().NotBeNull();
        _context!
            .ReferenceNameOverridesByPath.Should()
            .ContainKey("$.schoolReference")
            .WhoseValue.Should()
            .Be("SchoolReferenceOverride");
    }
}

internal static class SchemaInputValidationHelpers
{
    public static Exception CaptureExtractInputsException(
        JsonArray identityJsonPaths,
        JsonObject documentPathsMapping,
        JsonNode jsonSchemaForInsert,
        JsonArray? arrayUniquenessConstraints = null,
        JsonObject? relational = null
    )
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "Section",
            ["isDescriptor"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints ?? new JsonArray(),
            ["identityJsonPaths"] = identityJsonPaths,
            ["documentPathsMapping"] = documentPathsMapping,
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };

        if (relational is not null)
        {
            resourceSchema["relational"] = relational;
        }

        var apiSchemaRoot = CreateApiSchemaRoot(resourceSchema, "sections");
        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "sections",
        };

        var step = new ExtractInputsStep();

        try
        {
            step.Execute(context);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected ExtractInputsStep to fail.");
    }

    public static Exception CaptureValidationPipelineException(
        JsonArray identityJsonPaths,
        JsonObject documentPathsMapping,
        JsonNode jsonSchemaForInsert,
        JsonArray? arrayUniquenessConstraints = null,
        JsonObject? relational = null
    )
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "Section",
            ["isDescriptor"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints ?? new JsonArray(),
            ["identityJsonPaths"] = identityJsonPaths,
            ["documentPathsMapping"] = documentPathsMapping,
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };

        if (relational is not null)
        {
            resourceSchema["relational"] = relational;
        }

        var apiSchemaRoot = CreateApiSchemaRoot(resourceSchema, "sections");
        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "sections",
        };

        var pipeline = new RelationalModelBuilderPipeline(
            new IRelationalModelBuilderStep[] { new ExtractInputsStep(), new ValidateJsonSchemaStep() }
        );

        try
        {
            pipeline.Run(context);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected pipeline to fail.");
    }

    public static RelationalModelBuilderContext ExecuteExtractInputs(
        JsonArray identityJsonPaths,
        JsonObject documentPathsMapping,
        JsonNode jsonSchemaForInsert,
        JsonArray? arrayUniquenessConstraints = null,
        JsonObject? relational = null
    )
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "Section",
            ["isDescriptor"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = arrayUniquenessConstraints ?? new JsonArray(),
            ["identityJsonPaths"] = identityJsonPaths,
            ["documentPathsMapping"] = documentPathsMapping,
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };

        if (relational is not null)
        {
            resourceSchema["relational"] = relational;
        }

        var apiSchemaRoot = CreateApiSchemaRoot(resourceSchema, "sections");
        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "sections",
        };

        var step = new ExtractInputsStep();
        step.Execute(context);

        return context;
    }

    private static JsonNode CreateApiSchemaRoot(JsonObject resourceSchema, string endpointName)
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "5.0.0",
            ["projectEndpointName"] = "ed-fi",
            ["resourceSchemas"] = new JsonObject { [endpointName] = resourceSchema },
        };

        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema };
    }
}
