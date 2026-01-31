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

internal static class SchemaInputValidationHelpers
{
    public static Exception CaptureExtractInputsException(
        JsonArray identityJsonPaths,
        JsonObject documentPathsMapping,
        JsonNode jsonSchemaForInsert,
        JsonArray? arrayUniquenessConstraints = null
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
        JsonArray? arrayUniquenessConstraints = null
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
