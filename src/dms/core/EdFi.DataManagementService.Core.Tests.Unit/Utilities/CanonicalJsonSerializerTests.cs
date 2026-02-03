// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixture]
public class CanonicalJsonSerializerTests
{
    [TestFixture]
    public class Given_Object_With_Unsorted_Properties : CanonicalJsonSerializerTests
    {
        private JsonObject _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = new JsonObject
            {
                ["zebra"] = "last",
                ["alpha"] = "first",
                ["middle"] = "middle",
            };
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_sorts_properties_alphabetically()
        {
            _result.Should().Be("""{"alpha":"first","middle":"middle","zebra":"last"}""");
        }
    }

    [TestFixture]
    public class Given_Nested_Objects_With_Unsorted_Properties : CanonicalJsonSerializerTests
    {
        private JsonObject _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = new JsonObject
            {
                ["outer2"] = new JsonObject { ["inner2"] = "b", ["inner1"] = "a" },
                ["outer1"] = new JsonObject { ["nested2"] = "y", ["nested1"] = "x" },
            };
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_sorts_all_levels_recursively()
        {
            _result
                .Should()
                .Be("""{"outer1":{"nested1":"x","nested2":"y"},"outer2":{"inner1":"a","inner2":"b"}}""");
        }
    }

    [TestFixture]
    public class Given_Array_Elements : CanonicalJsonSerializerTests
    {
        private JsonArray _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = ["first", "second", "third"];
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_preserves_element_order()
        {
            _result.Should().Be("""["first","second","third"]""");
        }
    }

    [TestFixture]
    public class Given_Array_With_Object_Elements : CanonicalJsonSerializerTests
    {
        private JsonArray _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = [new JsonObject { ["z"] = 1, ["a"] = 2 }, new JsonObject { ["y"] = 3, ["b"] = 4 }];
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_preserves_array_order_but_sorts_object_properties()
        {
            _result.Should().Be("""[{"a":2,"z":1},{"b":4,"y":3}]""");
        }
    }

    [TestFixture]
    public class Given_Same_Object_With_Different_Property_Orders : CanonicalJsonSerializerTests
    {
        private JsonObject _input1 = null!;
        private JsonObject _input2 = null!;
        private byte[] _bytes1 = null!;
        private byte[] _bytes2 = null!;

        [SetUp]
        public void Setup()
        {
            _input1 = new JsonObject
            {
                ["a"] = 1,
                ["b"] = 2,
                ["c"] = 3,
            };
            _input2 = new JsonObject
            {
                ["c"] = 3,
                ["a"] = 1,
                ["b"] = 2,
            };
            _bytes1 = CanonicalJsonSerializer.SerializeToUtf8Bytes(_input1);
            _bytes2 = CanonicalJsonSerializer.SerializeToUtf8Bytes(_input2);
        }

        [Test]
        public void It_produces_identical_bytes()
        {
            _bytes1.Should().BeEquivalentTo(_bytes2);
        }
    }

    [TestFixture]
    public class Given_Ordinal_String_Comparison : CanonicalJsonSerializerTests
    {
        private JsonObject _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            // In ordinal sort, uppercase letters (ASCII 65-90) come before lowercase (97-122)
            _input = new JsonObject
            {
                ["alpha"] = 1,
                ["Alpha"] = 2,
                ["ALPHA"] = 3,
            };
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_uses_ordinal_comparison_uppercase_before_lowercase()
        {
            // ASCII order: 'A' (65) < 'a' (97)
            _result.Should().Be("""{"ALPHA":3,"Alpha":2,"alpha":1}""");
        }
    }

    [TestFixture]
    public class Given_Different_Whitespace_Inputs : CanonicalJsonSerializerTests
    {
        private JsonNode _prettyInput = null!;
        private JsonNode _minifiedInput = null!;
        private string _prettyResult = null!;
        private string _minifiedResult = null!;

        [SetUp]
        public void Setup()
        {
            _prettyInput = JsonNode.Parse(
                """
                {
                    "name": "test",
                    "value": 42
                }
                """
            )!;

            _minifiedInput = JsonNode.Parse("""{"name":"test","value":42}""")!;

            _prettyResult = CanonicalJsonSerializer.SerializeToString(_prettyInput);
            _minifiedResult = CanonicalJsonSerializer.SerializeToString(_minifiedInput);
        }

        [Test]
        public void It_produces_identical_minified_output()
        {
            _prettyResult.Should().Be(_minifiedResult);
        }

        [Test]
        public void It_contains_no_whitespace()
        {
            _prettyResult.Should().NotContain(" ");
            _prettyResult.Should().NotContain("\n");
            _prettyResult.Should().NotContain("\r");
            _prettyResult.Should().NotContain("\t");
        }
    }

    [TestFixture]
    public class Given_Null_Node : CanonicalJsonSerializerTests
    {
        private byte[] _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = CanonicalJsonSerializer.SerializeToUtf8Bytes(null);
        }

        [Test]
        public void It_returns_null_literal()
        {
            Encoding.UTF8.GetString(_result).Should().Be("null");
        }
    }

    [TestFixture]
    public class Given_Object_With_Null_Values : CanonicalJsonSerializerTests
    {
        private JsonObject _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = new JsonObject { ["present"] = "value", ["absent"] = null };
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_includes_null_values()
        {
            _result.Should().Be("""{"absent":null,"present":"value"}""");
        }
    }

    [TestFixture]
    public class Given_Primitive_Values : CanonicalJsonSerializerTests
    {
        [Test]
        public void It_serializes_strings_correctly()
        {
            var node = JsonValue.Create("test");
            CanonicalJsonSerializer.SerializeToString(node).Should().Be("\"test\"");
        }

        [Test]
        public void It_serializes_integers_correctly()
        {
            var node = JsonValue.Create(42);
            CanonicalJsonSerializer.SerializeToString(node).Should().Be("42");
        }

        [Test]
        public void It_serializes_decimals_correctly()
        {
            var node = JsonValue.Create(3.14);
            CanonicalJsonSerializer.SerializeToString(node).Should().Be("3.14");
        }

        [Test]
        public void It_serializes_booleans_correctly()
        {
            var trueNode = JsonValue.Create(true);
            var falseNode = JsonValue.Create(false);
            CanonicalJsonSerializer.SerializeToString(trueNode).Should().Be("true");
            CanonicalJsonSerializer.SerializeToString(falseNode).Should().Be("false");
        }
    }

    [TestFixture]
    public class Given_UTF8_Output : CanonicalJsonSerializerTests
    {
        private byte[] _result = null!;

        [SetUp]
        public void Setup()
        {
            var input = new JsonObject { ["key"] = "value" };
            _result = CanonicalJsonSerializer.SerializeToUtf8Bytes(input);
        }

        [Test]
        public void It_produces_valid_utf8_without_bom()
        {
            // UTF-8 BOM is EF BB BF - verify the output doesn't start with it
            var hasBom =
                _result.Length >= 3 && _result[0] == 0xEF && _result[1] == 0xBB && _result[2] == 0xBF;
            hasBom.Should().BeFalse("output should not have UTF-8 BOM");
        }

        [Test]
        public void It_is_valid_utf8()
        {
            // Should not throw
            var decoded = Encoding.UTF8.GetString(_result);
            decoded.Should().NotBeNullOrEmpty();
        }
    }

    [TestFixture]
    public class Given_Complex_Nested_Structure : CanonicalJsonSerializerTests
    {
        private JsonObject _input = null!;
        private string _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = new JsonObject
            {
                ["z_outer"] = new JsonObject
                {
                    ["b_inner"] = new JsonArray(
                        new JsonObject { ["d"] = 1, ["c"] = 2 },
                        new JsonObject { ["f"] = 3, ["e"] = 4 }
                    ),
                    ["a_inner"] = "value",
                },
                ["a_outer"] = 123,
            };
            _result = CanonicalJsonSerializer.SerializeToString(_input);
        }

        [Test]
        public void It_handles_deeply_nested_structures()
        {
            _result
                .Should()
                .Be(
                    """{"a_outer":123,"z_outer":{"a_inner":"value","b_inner":[{"c":2,"d":1},{"e":4,"f":3}]}}"""
                );
        }
    }

    [TestFixture]
    public class Given_Canonicalize_Method : CanonicalJsonSerializerTests
    {
        private JsonObject _input = null!;
        private JsonNode _result = null!;

        [SetUp]
        public void Setup()
        {
            _input = new JsonObject { ["z"] = 1, ["a"] = 2 };
            _result = CanonicalJsonSerializer.Canonicalize(_input)!;
        }

        [Test]
        public void It_returns_sorted_structure()
        {
            var keys = _result.AsObject().Select(p => p.Key).ToList();
            keys.Should().Equal("a", "z");
        }

        [Test]
        public void It_does_not_modify_original()
        {
            var originalKeys = _input.Select(p => p.Key).ToList();
            originalKeys.Should().Equal("z", "a");
        }
    }
}
