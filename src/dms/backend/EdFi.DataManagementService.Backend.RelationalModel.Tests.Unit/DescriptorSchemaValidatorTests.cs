// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Valid_Descriptor_Schema
{
    private static JsonNode CreateValidDescriptorSchema()
    {
        return JsonNode.Parse(
            """
            {
              "isDescriptor": true,
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  },
                  "codeValue": {
                    "type": "string"
                  },
                  "shortDescription": {
                    "type": "string"
                  },
                  "description": {
                    "type": "string"
                  },
                  "effectiveBeginDate": {
                    "type": "string",
                    "format": "date"
                  },
                  "effectiveEndDate": {
                    "type": "string",
                    "format": "date"
                  }
                },
                "required": ["namespace", "codeValue"]
              }
            }
            """
        )!;
    }

    [Test]
    public void It_Should_Validate_Core_Fields()
    {
        var schema = CreateValidDescriptorSchema();
        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Test]
    public void It_Should_Accept_Optional_Fields()
    {
        var schema = CreateValidDescriptorSchema();
        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_Should_Accept_Extension_Placeholder()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  },
                  "codeValue": {
                    "type": "string"
                  },
                  "_ext": {
                    "type": "object"
                  }
                },
                "required": ["namespace", "codeValue"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeTrue();
    }

    [Test]
    public void It_Should_Recognize_Descriptor_By_Name_And_Structure()
    {
        var schema = CreateValidDescriptorSchema();
        var isDescriptor = DescriptorSchemaValidator.IsDescriptorResource(
            schema,
            "AcademicHonorCategoryDescriptor"
        );

        isDescriptor.Should().BeTrue();
    }
}

[TestFixture]
public class Given_An_Invalid_Descriptor_Schema
{
    [Test]
    public void It_Should_Reject_Missing_Namespace()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "codeValue": {
                    "type": "string"
                  }
                },
                "required": ["codeValue"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("namespace"));
    }

    [Test]
    public void It_Should_Reject_Missing_CodeValue()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  }
                },
                "required": ["namespace"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("codeValue"));
    }

    [Test]
    public void It_Should_Reject_Extra_Required_Fields()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  },
                  "codeValue": {
                    "type": "string"
                  },
                  "customField": {
                    "type": "string"
                  }
                },
                "required": ["namespace", "codeValue", "customField"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeFalse();
        result
            .Errors.Should()
            .Contain(e => e.Contains("unexpected required fields") && e.Contains("customField"));
    }

    [Test]
    public void It_Should_Reject_Type_Mismatches()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "integer"
                  },
                  "codeValue": {
                    "type": "string"
                  }
                },
                "required": ["namespace", "codeValue"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("namespace") && e.Contains("type"));
    }

    [Test]
    public void It_Should_Reject_Namespace_Not_Required()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  },
                  "codeValue": {
                    "type": "string"
                  }
                },
                "required": ["codeValue"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("namespace") && e.Contains("must be required"));
    }

    [Test]
    public void It_Should_Reject_CodeValue_Not_Required()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  },
                  "codeValue": {
                    "type": "string"
                  }
                },
                "required": ["namespace"]
              }
            }
            """
        )!;

        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("codeValue") && e.Contains("must be required"));
    }

    [Test]
    public void It_Should_Reject_Null_Schema()
    {
        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(null);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("null"));
    }

    [Test]
    public void It_Should_Not_Recognize_Non_Descriptor_By_Name()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "namespace": {
                    "type": "string"
                  },
                  "codeValue": {
                    "type": "string"
                  }
                },
                "required": ["namespace", "codeValue"]
              }
            }
            """
        )!;

        var isDescriptor = DescriptorSchemaValidator.IsDescriptorResource(schema, "School");

        isDescriptor.Should().BeFalse();
    }

    [Test]
    public void It_Should_Not_Recognize_Descriptor_Without_Required_Properties()
    {
        var schema = JsonNode.Parse(
            """
            {
              "jsonSchemaForInsert": {
                "type": "object",
                "properties": {
                  "someField": {
                    "type": "string"
                  }
                },
                "required": ["someField"]
              }
            }
            """
        )!;

        var isDescriptor = DescriptorSchemaValidator.IsDescriptorResource(
            schema,
            "AcademicHonorCategoryDescriptor"
        );

        isDescriptor.Should().BeFalse();
    }
}
