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
/// Test fixture for a document reference without reference json paths.
/// </summary>
[TestFixture]
public class Given_A_Document_Reference_Without_ReferenceJsonPaths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing reference json paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_missing_reference_json_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("referenceJsonPaths");
        _exception.Message.Should().Contain("School");
    }
}

/// <summary>
/// Test fixture for a document reference with inconsistent reference json paths prefix.
/// </summary>
[TestFixture]
public class Given_A_Document_Reference_With_Inconsistent_ReferenceJsonPaths_Prefix
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with inconsistent prefix.
    /// </summary>
    [Test]
    public void It_should_fail_with_inconsistent_prefix()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("inconsistent referenceJsonPaths prefix");
        _exception.Message.Should().Contain("School");
    }
}

/// <summary>
/// Test fixture for a document reference with duplicate identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Document_Reference_With_Duplicate_IdentityJsonPaths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with duplicate identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate identityJsonPath");
        _exception.Message.Should().Contain("$.schoolId");
    }
}

/// <summary>
/// Test fixture for a document reference marked as identity with partial identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Document_Reference_Marked_As_Identity_With_Partial_IdentityJsonPaths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_missing_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("identityJsonPaths");
        _exception.Message.Should().Contain("missing reference path");
        _exception.Message.Should().Contain("$.schoolReference");
    }
}

/// <summary>
/// Test fixture for a document reference marked as identity without identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Document_Reference_Marked_As_Identity_Without_IdentityJsonPaths
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    /// <summary>
    /// It should not fail without identity json paths.
    /// </summary>
    [Test]
    public void It_should_not_fail_without_identity_json_paths()
    {
        _context.Should().NotBeNull();
        _context!
            .DocumentReferenceMappings.Should()
            .ContainSingle()
            .Which.IsPartOfIdentity.Should()
            .BeFalse();
    }
}

/// <summary>
/// Test fixture for a document reference not marked as identity with identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Document_Reference_Not_Marked_As_Identity_With_IdentityJsonPaths
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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
        };

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: identityJsonPaths,
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    /// <summary>
    /// It should not fail with identity paths present.
    /// </summary>
    [Test]
    public void It_should_not_fail_with_identity_paths_present()
    {
        _context.Should().NotBeNull();
        _context!.DocumentReferenceMappings.Should().ContainSingle().Which.IsPartOfIdentity.Should().BeTrue();
    }
}

/// <summary>
/// Test fixture for an identity reference that is not required.
/// </summary>
[TestFixture]
public class Given_An_Identity_Reference_That_Is_Not_Required
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with optional identity reference.
    /// </summary>
    [Test]
    public void It_should_fail_with_optional_identity_reference()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("identityJsonPaths");
        _exception.Message.Should().Contain("isRequired");
        _exception.Message.Should().Contain("School");
    }
}

/// <summary>
/// Test fixture for a scalar marked as identity without identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Scalar_Marked_As_Identity_Without_IdentityJsonPaths
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    /// <summary>
    /// It should not fail without identity json paths.
    /// </summary>
    [Test]
    public void It_should_not_fail_without_identity_json_paths()
    {
        _context.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for a scalar not marked as identity with identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Scalar_Not_Marked_As_Identity_With_IdentityJsonPaths
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray { "$.schoolId" },
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    /// <summary>
    /// It should not fail with identity paths present.
    /// </summary>
    [Test]
    public void It_should_not_fail_with_identity_paths_present()
    {
        _context.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for a descriptor marked as identity without identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Descriptor_Marked_As_Identity_Without_IdentityJsonPaths
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "SchoolTypeDescriptor",
                ["path"] = "$.schoolTypeDescriptor",
            },
        };

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    /// <summary>
    /// It should not fail without identity json paths.
    /// </summary>
    [Test]
    public void It_should_not_fail_without_identity_json_paths()
    {
        _context.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for a descriptor not marked as identity with identity json paths.
/// </summary>
[TestFixture]
public class Given_A_Descriptor_Not_Marked_As_Identity_With_IdentityJsonPaths
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "SchoolTypeDescriptor",
                ["path"] = "$.schoolTypeDescriptor",
            },
        };

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray { "$.schoolTypeDescriptor" },
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject()
        );
    }

    /// <summary>
    /// It should not fail with identity paths present.
    /// </summary>
    [Test]
    public void It_should_not_fail_with_identity_paths_present()
    {
        _context.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for identity json paths not mapped in document paths mapping.
/// </summary>
[TestFixture]
public class Given_IdentityJsonPaths_Not_Mapped_In_DocumentPathsMapping
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with unmapped identity json paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_unmapped_identity_json_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("identityJsonPaths");
        _exception.Message.Should().Contain("$.missingId");
    }
}

/// <summary>
/// Test fixture for duplicate identity json paths.
/// </summary>
[TestFixture]
public class Given_Duplicate_IdentityJsonPaths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with duplicate identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate");
        _exception.Message.Should().Contain("$.schoolId");
        _exception.Message.Should().Contain("Ed-Fi:Section");
    }
}

/// <summary>
/// Test fixture for nested array uniqueness constraint without base path.
/// </summary>
[TestFixture]
public class Given_Nested_ArrayUniquenessConstraint_Without_BasePath
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing base path.
    /// </summary>
    [Test]
    public void It_should_fail_with_missing_base_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("nestedConstraints");
        _exception.Message.Should().Contain("basePath");
    }
}

/// <summary>
/// Test fixture for array uniqueness constraint with partial reference identity paths.
/// </summary>
[TestFixture]
public class Given_ArrayUniquenessConstraint_With_Partial_Reference_Identity_Paths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing reference identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_missing_reference_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints scope");
        _exception.Message.Should().Contain("$.schools[*].schoolReference");
        _exception.Message.Should().Contain("$.schools[*].schoolReference.schoolYear");
    }
}

/// <summary>
/// Test fixture for nested array uniqueness constraint with partial reference identity paths.
/// </summary>
[TestFixture]
public class Given_Nested_ArrayUniquenessConstraint_With_Partial_Reference_Identity_Paths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing nested reference identity paths.
    /// </summary>
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

/// <summary>
/// Test fixture for array uniqueness constraint with unknown paths.
/// </summary>
[TestFixture]
public class Given_ArrayUniquenessConstraint_With_Unknown_Paths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with unknown paths.
    /// </summary>
    [Test]
    public void It_should_fail_with_unknown_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints paths");
        _exception.Message.Should().Contain("$.items[*].missing");
    }
}

/// <summary>
/// Test fixture for array uniqueness constraint with unknown base path.
/// </summary>
[TestFixture]
public class Given_ArrayUniquenessConstraint_With_Unknown_BasePath
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with unknown base path.
    /// </summary>
    [Test]
    public void It_should_fail_with_unknown_base_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("arrayUniquenessConstraints basePath");
        _exception.Message.Should().Contain("$.missing[*]");
    }
}

/// <summary>
/// Test fixture for reference identity json path not in target identity json paths.
/// </summary>
[TestFixture]
public class Given_Reference_IdentityJsonPath_Not_In_Target_IdentityJsonPaths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with missing target identity path.
    /// </summary>
    [Test]
    public void It_should_fail_with_missing_target_identity_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("identityJsonPath");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for a relational name override with invalid json path.
/// </summary>
[TestFixture]
public class Given_A_Relational_NameOverride_With_Invalid_JsonPath
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with invalid json path.
    /// </summary>
    [Test]
    public void It_should_fail_with_invalid_json_path()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("relational.nameOverrides");
        _exception.Message.Should().Contain("$.schoolReference..schoolId");
        _exception.Message.Should().Contain("Ed-Fi:Section");
    }
}

/// <summary>
/// Test fixture for a relational name override for non reference path.
/// </summary>
[TestFixture]
public class Given_A_Relational_NameOverride_For_NonReference_Path
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        _context = SchemaInputValidationHelpers.ExecuteExtractInputs(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject(),
            relational: relational
        );
    }

    /// <summary>
    /// It should capture the override.
    /// </summary>
    [Test]
    public void It_should_capture_the_override()
    {
        _context.Should().NotBeNull();
        _context!.NameOverridesByPath.Should().ContainKey("$.schoolId");

        var entry = _context.NameOverridesByPath["$.schoolId"];
        entry.RawKey.Should().Be("$.schoolId");
        entry.CanonicalPath.Should().Be("$.schoolId");
        entry.NormalizedName.Should().Be("School");
        entry.Kind.Should().Be(NameOverrideKind.Column);
    }
}

/// <summary>
/// Test fixture for a relational name override for a reference path.
/// </summary>
[TestFixture]
public class Given_A_Relational_NameOverride_For_A_Reference_Path
{
    private RelationalModelBuilderContext? _context;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should apply the override.
    /// </summary>
    [Test]
    public void It_should_apply_the_override()
    {
        _context.Should().NotBeNull();
        _context!.NameOverridesByPath.Should().ContainKey("$.schoolReference");

        var entry = _context.NameOverridesByPath["$.schoolReference"];
        entry.RawKey.Should().Be("$.schoolReference");
        entry.CanonicalPath.Should().Be("$.schoolReference");
        entry.NormalizedName.Should().Be("SchoolReferenceOverride");
        entry.Kind.Should().Be(NameOverrideKind.Column);
    }
}

/// <summary>
/// Test fixture for a relational name override inside a reference object.
/// </summary>
[TestFixture]
public class Given_A_Relational_NameOverride_Inside_A_Reference_Object
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var relational = new JsonObject
        {
            ["nameOverrides"] = new JsonObject { ["$.schoolReference.schoolId"] = "SchoolIdOverride" },
        };

        _exception = SchemaInputValidationHelpers.CaptureExtractInputsException(
            identityJsonPaths: new JsonArray(),
            documentPathsMapping: documentPathsMapping,
            jsonSchemaForInsert: new JsonObject(),
            relational: relational
        );
    }

    /// <summary>
    /// It should fail fast when the override targets inside the reference object.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_inside_reference_object_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("inside reference object");
        _exception.Message.Should().Contain("$.schoolReference.schoolId");
        _exception.Message.Should().Contain("$.schoolReference");
    }
}

/// <summary>
/// Test fixture for a descriptor resource with a relational block.
/// </summary>
[TestFixture]
public class Given_A_Descriptor_With_A_Relational_Block
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "Descriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["relational"] = new JsonObject { ["rootTableNameOverride"] = "Descriptor" },
        };

        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "5.0.0",
            ["projectEndpointName"] = "ed-fi",
            ["resourceSchemas"] = new JsonObject { ["descriptors"] = resourceSchema },
        };

        var apiSchemaRoot = new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = projectSchema,
        };

        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "descriptors",
        };

        var step = new ExtractInputsStep();

        try
        {
            step.Execute(context);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail fast with a descriptor relational override.
    /// </summary>
    [Test]
    public void It_should_fail_fast_with_descriptor_relational_override()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Descriptor resource");
        _exception.Message.Should().Contain("Ed-Fi:Descriptor");
    }
}

/// <summary>
/// Test fixture for a root table name override on a resource extension.
/// </summary>
[TestFixture]
public class Given_A_Relational_RootTableNameOverride_On_A_Resource_Extension
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "Section",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["relational"] = new JsonObject { ["rootTableNameOverride"] = "SectionOverride" },
        };

        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "5.0.0",
            ["projectEndpointName"] = "ed-fi",
            ["resourceSchemas"] = new JsonObject { ["sectionExtensions"] = resourceSchema },
        };

        var apiSchemaRoot = new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = projectSchema,
        };

        var context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "sectionExtensions",
        };

        var step = new ExtractInputsStep();

        try
        {
            step.Execute(context);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail fast with a root table name override on a resource extension.
    /// </summary>
    [Test]
    public void It_should_fail_fast_with_resource_extension_root_override()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("rootTableNameOverride");
        _exception.Message.Should().Contain("Ed-Fi:Section");
    }
}

/// <summary>
/// Test type schema input validation helpers.
/// </summary>
internal static class SchemaInputValidationHelpers
{
    /// <summary>
    /// Capture extract inputs exception.
    /// </summary>
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

    /// <summary>
    /// Capture validation pipeline exception.
    /// </summary>
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

    /// <summary>
    /// Execute extract inputs.
    /// </summary>
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

    /// <summary>
    /// Create api schema root.
    /// </summary>
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
