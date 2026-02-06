// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Integration tests verifying that DerivedRelationalModelSetBuilder.Build() fails fast
/// when the effective schema contains descriptor resources whose JSON shape cannot be
/// represented by the dms.Descriptor column contract.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_Invalid_Descriptor_Missing_Namespace
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["invalidDescriptors"] = new JsonObject
                {
                    ["resourceName"] = "InvalidDescriptor",
                    ["isDescriptor"] = true,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray(),
                    ["documentPathsMapping"] = new JsonObject(),
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["invalidDescriptorId"] = new JsonObject { ["type"] = "integer" },
                            ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                            ["shortDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
                        },
                        ["required"] = new JsonArray
                        {
                            "invalidDescriptorId",
                            "codeValue",
                            "shortDescription",
                        },
                    },
                },
            },
        };

        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "InvalidDescriptor"),
            "5.0.0",
            false
        );

        var project = new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema);

        var schemaComponents = new[] { new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false) };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            1,
            new byte[] { 0x01 },
            schemaComponents,
            new[] { resourceKey }
        );

        var effectiveSchemaSet = new EffectiveSchemaSet(effectiveSchemaInfo, new[] { project });

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

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
    public void It_Should_Throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_Should_Mention_Descriptor_Incompatibility()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("incompatible with dms.Descriptor contract");
    }

    [Test]
    public void It_Should_Mention_Missing_Namespace_Field()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("namespace");
    }

    [Test]
    public void It_Should_Identify_The_Invalid_Resource()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("InvalidDescriptor");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_Invalid_Descriptor_Missing_CodeValue
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["invalidDescriptors"] = new JsonObject
                {
                    ["resourceName"] = "InvalidDescriptor",
                    ["isDescriptor"] = true,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray(),
                    ["documentPathsMapping"] = new JsonObject(),
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["invalidDescriptorId"] = new JsonObject { ["type"] = "integer" },
                            ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                            ["shortDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
                        },
                        ["required"] = new JsonArray
                        {
                            "invalidDescriptorId",
                            "namespace",
                            "shortDescription",
                        },
                    },
                },
            },
        };

        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "InvalidDescriptor"),
            "5.0.0",
            false
        );

        var project = new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema);

        var schemaComponents = new[] { new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false) };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            1,
            new byte[] { 0x01 },
            schemaComponents,
            new[] { resourceKey }
        );

        var effectiveSchemaSet = new EffectiveSchemaSet(effectiveSchemaInfo, new[] { project });

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

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
    public void It_Should_Throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_Should_Mention_Descriptor_Incompatibility()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("incompatible with dms.Descriptor contract");
    }

    [Test]
    public void It_Should_Mention_Missing_CodeValue_Field()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("codeValue");
    }

    [Test]
    public void It_Should_Identify_The_Invalid_Resource()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("InvalidDescriptor");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_Invalid_Descriptor_Extra_Required_Fields
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["invalidDescriptors"] = new JsonObject
                {
                    ["resourceName"] = "InvalidDescriptor",
                    ["isDescriptor"] = true,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray(),
                    ["documentPathsMapping"] = new JsonObject(),
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["invalidDescriptorId"] = new JsonObject { ["type"] = "integer" },
                            ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                            ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                            ["shortDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
                            ["customRequiredField"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 100,
                            },
                        },
                        ["required"] = new JsonArray
                        {
                            "invalidDescriptorId",
                            "namespace",
                            "codeValue",
                            "shortDescription",
                            "customRequiredField",
                        },
                    },
                },
            },
        };

        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "InvalidDescriptor"),
            "5.0.0",
            false
        );

        var project = new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema);

        var schemaComponents = new[] { new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false) };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            1,
            new byte[] { 0x01 },
            schemaComponents,
            new[] { resourceKey }
        );

        var effectiveSchemaSet = new EffectiveSchemaSet(effectiveSchemaInfo, new[] { project });

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

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
    public void It_Should_Throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_Should_Mention_Descriptor_Incompatibility()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("incompatible with dms.Descriptor contract");
    }

    [Test]
    public void It_Should_Mention_Unexpected_Required_Fields()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("unexpected required fields");
    }

    [Test]
    public void It_Should_Identify_The_Extra_Field()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("customRequiredField");
    }

    [Test]
    public void It_Should_Identify_The_Invalid_Resource()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("InvalidDescriptor");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_Invalid_Descriptor_Wrong_Type
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["invalidDescriptors"] = new JsonObject
                {
                    ["resourceName"] = "InvalidDescriptor",
                    ["isDescriptor"] = true,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray(),
                    ["documentPathsMapping"] = new JsonObject(),
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["invalidDescriptorId"] = new JsonObject { ["type"] = "integer" },
                            ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                            ["codeValue"] = new JsonObject { ["type"] = "integer" },
                            ["shortDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
                        },
                        ["required"] = new JsonArray
                        {
                            "invalidDescriptorId",
                            "namespace",
                            "codeValue",
                            "shortDescription",
                        },
                    },
                },
            },
        };

        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "InvalidDescriptor"),
            "5.0.0",
            false
        );

        var project = new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, projectSchema);

        var schemaComponents = new[] { new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false) };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            1,
            new byte[] { 0x01 },
            schemaComponents,
            new[] { resourceKey }
        );

        var effectiveSchemaSet = new EffectiveSchemaSet(effectiveSchemaInfo, new[] { project });

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

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
    public void It_Should_Throw_InvalidOperationException()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_Should_Mention_Descriptor_Incompatibility()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("incompatible with dms.Descriptor contract");
    }

    [Test]
    public void It_Should_Mention_Type_Mismatch()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("must be of type");
    }

    [Test]
    public void It_Should_Mention_CodeValue_Field()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("codeValue");
    }

    [Test]
    public void It_Should_Identify_The_Invalid_Resource()
    {
        _exception.Should().NotBeNull();
        _exception!.Message.Should().Contain("InvalidDescriptor");
    }
}
