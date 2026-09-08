// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Unit tests for validating descriptor schemas that match the canonical <c>dms.Descriptor</c> contract.
/// </summary>
[TestFixture]
public class Given_A_Valid_Descriptor_Schema
{
    /// <summary>
    /// Creates a JSON schema payload representing a valid descriptor resource.
    /// </summary>
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

    /// <summary>
    /// It should validate core descriptor fields and return an empty error set.
    /// </summary>
    [Test]
    public void It_Should_Validate_Core_Fields()
    {
        var schema = CreateValidDescriptorSchema();
        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// It should accept optional descriptor fields when present.
    /// </summary>
    [Test]
    public void It_Should_Accept_Optional_Fields()
    {
        var schema = CreateValidDescriptorSchema();
        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(schema);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// It should accept an <c>_ext</c> placeholder property for extensions.
    /// </summary>
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

    /// <summary>
    /// It should recognize a descriptor resource by schema shape and metadata.
    /// </summary>
    [Test]
    public void It_Should_Recognize_Descriptor_By_Name_And_Structure()
    {
        var schema = CreateValidDescriptorSchema();
        var isDescriptor = DescriptorSchemaValidator.IsDescriptorResource(schema);

        isDescriptor.Should().BeTrue();
    }
}

/// <summary>
/// Unit tests for validating descriptor schemas that are incompatible with the canonical <c>dms.Descriptor</c>
/// contract.
/// </summary>
[TestFixture]
public class Given_An_Invalid_Descriptor_Schema
{
    /// <summary>
    /// It should reject schemas missing the required <c>namespace</c> field.
    /// </summary>
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

    /// <summary>
    /// It should reject schemas missing the required <c>codeValue</c> field.
    /// </summary>
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

    /// <summary>
    /// It should reject schemas that declare unexpected required fields.
    /// </summary>
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

    /// <summary>
    /// It should reject schemas with incompatible JSON types for required descriptor fields.
    /// </summary>
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

    /// <summary>
    /// It should reject schemas where <c>namespace</c> is not listed as required.
    /// </summary>
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

    /// <summary>
    /// It should reject schemas where <c>codeValue</c> is not listed as required.
    /// </summary>
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

    /// <summary>
    /// It should reject null resource schema payloads.
    /// </summary>
    [Test]
    public void It_Should_Reject_Null_Schema()
    {
        var result = DescriptorSchemaValidator.ValidateDescriptorSchema(null);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("null"));
    }

    /// <summary>
    /// It should not recognize resources without <c>isDescriptor</c> metadata as descriptors.
    /// </summary>
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

        var isDescriptor = DescriptorSchemaValidator.IsDescriptorResource(schema);

        isDescriptor.Should().BeFalse();
    }

    /// <summary>
    /// It should not recognize schemas that do not contain the required descriptor properties.
    /// </summary>
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

        var isDescriptor = DescriptorSchemaValidator.IsDescriptorResource(schema);

        isDescriptor.Should().BeFalse();
    }
}
