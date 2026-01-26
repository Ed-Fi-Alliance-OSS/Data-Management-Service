// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_A_JsonSchema_With_A_Ref
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "reference",
            new JsonObject { ["$ref"] = "#/definitions/reference" }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_ref_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.reference.$ref");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_OneOf
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "choice",
            new JsonObject
            {
                ["oneOf"] = new JsonArray
                {
                    new JsonObject { ["type"] = "string" },
                    new JsonObject { ["type"] = "integer" },
                },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_oneof_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.choice.oneOf");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_AnyOf
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "choice",
            new JsonObject
            {
                ["anyOf"] = new JsonArray
                {
                    new JsonObject { ["type"] = "string" },
                    new JsonObject { ["type"] = "integer" },
                },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_anyof_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.choice.anyOf");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_AllOf
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "choice",
            new JsonObject
            {
                ["allOf"] = new JsonArray
                {
                    new JsonObject { ["type"] = "string" },
                    new JsonObject { ["type"] = "integer" },
                },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_allof_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.choice.allOf");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_Enum
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "status",
            new JsonObject
            {
                ["enum"] = new JsonArray { "A", "B" },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_enum_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.status.enum");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_PatternProperties
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "values",
            new JsonObject
            {
                ["patternProperties"] = new JsonObject { ["^x-"] = new JsonObject { ["type"] = "string" } },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_patternproperties_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.values.patternProperties");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_A_Type_Array
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "value",
            new JsonObject
            {
                ["type"] = new JsonArray { "string", "null" },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_type_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.properties.value.type");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_A_Non_Object_Root
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject { ["type"] = "string" };

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_a_root_schema_error()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("Root schema must be an object");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_Array_Items_Not_Object
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "collection",
            new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_fail_with_the_items_type_path()
    {
        _exception
            .Should()
            .BeOfType<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("$.collection");
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_A_Descriptor_Scalar_Array
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithProperty(
            "gradeLevelDescriptors",
            new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            }
        );

        var descriptorPath = JsonPathExpressionCompiler.Compile("$.gradeLevelDescriptors[*]");
        var descriptorInfo = new DescriptorPathInfo(
            descriptorPath,
            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(
            schema,
            context =>
            {
                context.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [descriptorPath.Canonical] = descriptorInfo,
                };
            }
        );
    }

    [Test]
    public void It_should_not_throw()
    {
        _exception.Should().BeNull();
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_AdditionalProperties_True
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithAdditionalProperties(
            JsonValue.Create(true)
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_not_throw()
    {
        _exception.Should().BeNull();
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_AdditionalProperties_False
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithAdditionalProperties(
            JsonValue.Create(false)
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_not_throw()
    {
        _exception.Should().BeNull();
    }
}

[TestFixture]
public class Given_A_JsonSchema_With_AdditionalProperties_Object
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = ValidateJsonSchemaStepTestHelper.CreateRootSchemaWithAdditionalProperties(
            new JsonObject { ["$ref"] = "#/definitions/ignored" }
        );

        _exception = ValidateJsonSchemaStepTestHelper.Execute(schema);
    }

    [Test]
    public void It_should_not_throw()
    {
        _exception.Should().BeNull();
    }
}

internal static class ValidateJsonSchemaStepTestHelper
{
    public static JsonObject CreateRootSchemaWithProperty(string propertyName, JsonObject propertySchema)
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [propertyName] = propertySchema },
        };
    }

    public static JsonObject CreateRootSchemaWithAdditionalProperties(JsonNode additionalProperties)
    {
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = additionalProperties };
    }

    public static Exception? Execute(
        JsonObject schema,
        Action<RelationalModelBuilderContext>? configure = null
    )
    {
        var context = new RelationalModelBuilderContext { JsonSchemaForInsert = schema };

        configure?.Invoke(context);

        var step = new ValidateJsonSchemaStep();

        try
        {
            step.Execute(context);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
