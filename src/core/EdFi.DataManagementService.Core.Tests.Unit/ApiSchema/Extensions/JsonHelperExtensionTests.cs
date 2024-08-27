// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.ApiSchema.Extensions.Tests;

[TestFixture]
public class JsonHelperExtensionsTests
{
    [TestFixture]
    public class When_selecting_a_JSON_node_using_JsonPath
    {
        private static JsonNode? SelectNodeFromPath(JsonNode jsonNode, string jsonPath)
        {
            var logger = A.Fake<ILogger>();
            return jsonNode.SelectNodeFromPath(jsonPath, logger);
        }

        [Test]
        public void Given_multiple_path_matches__Then_throws_InvalidOperationException()
        {
            // Arrange
            string jsonString =
                @"[
                {
                    ""name"": ""John Doe""
                },
                {
                    ""name"": ""Jane Doe""
                }
            ]";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.*.name";

            // Act
            Action act = () => SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Given_invalid_JsonPath__Then_throws_InvalidOperationException()
        {
            // Arrange
            string jsonString =
                @"{
                ""name"": ""Jane Doe""
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$*.name";

            // Act
            Action act = () => SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Given_JSON_with_duplicate_keys__Then_throws_InvalidOperationException()
        {
            // Arrange
            string jsonString =
                @"{
                ""name"": ""John Doe"",
                ""name"": ""Jane Doe""
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.name";

            // Act
            Action act = () => SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>().WithInnerException<ArgumentException>();
        }

        [Test]
        public void Given_valid_inputs__Then_correctly_selects_string()
        {
            // Arrange
            string jsonString =
                @"{
                ""name"": ""John Doe"",
                ""age"": 30,
                ""address"": {
                    ""street"": ""123 Main St"",
                    ""city"": ""Austin"",
                    ""state"": ""CA""
                }
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.address.city";

            // Act
            JsonNode? result = SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            result.Should().NotBeNull();
            result!.GetValue<string>().Should().Be("Austin");
        }

        [Test]
        public void Given_valid_input_and_no_match__Then_returns_null()
        {
            // Arrange
            string jsonString =
                @"{
                ""name"": ""John Doe"",
                ""age"": 30
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.address.city";

            // Act
            JsonNode? result = SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void Given_matched_key_has_null_value__Then_returns_null()
        {
            // Arrange
            string jsonString =
                @"{
                ""name"": null
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.name";

            // Act
            JsonNode? result = SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void Given_matched_array_is_empty__Then_returns_null()
        {
            // Arrange
            string jsonString =
                @"{
                ""items"": []
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[0]";

            // Act
            JsonNode? result = SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void Given_nested_matched_key__Then_returns_null()
        {
            // Arrange
            string jsonString =
                @"{
                ""data"": {
                    ""item"": null
                }
            }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.data.item";

            // Act
            JsonNode? result = SelectNodeFromPath(jsonNode, jsonPath);

            // Assert
            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class When_selecting_nodes_from_array_path
    {
        private static IEnumerable<JsonNode?> SelectNodesFromArrayPath(JsonNode jsonNode, string jsonPath)
        {
            var logger = A.Fake<ILogger>();
            return jsonNode.SelectNodesFromArrayPath(jsonPath, logger);
        }

        [Test]
        public void Given_there_is_no_path_match__Then_return_empty_list()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": 1 }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items.*.doesNotExist";

            // Act
            var result = SelectNodesFromArrayPath(jsonNode, jsonPath);

            // Assert
            result.Count().Should().Be(0);
        }

        [Test]
        public void Given_invalid_JsonPath__Then_throws_InvalidOperationException()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": 1 }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$*.name";

            // Act
            Action act = () => SelectNodesFromArrayPath(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Given_null_node_in_array__Then_skip_that_value()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": 1 },
            null,
            { ""value"": 3 }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[*].value";

            // Act
            var result = SelectNodesFromArrayPath(jsonNode, jsonPath);

            // Assert
            result.Count().Should().Be(2);
            var list = result.ToList();
            list[0]!.GetValue<int>().Should().Be(1);
            list[1]!.GetValue<int>().Should().Be(3);
        }

        [Test]
        public void Given_null_value_nested_in_array__Then_return_null_for_that_key()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": 1 },
            { ""value"": null },
            { ""value"": 3 }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[*].value";

            // Act
            var result = SelectNodesFromArrayPath(jsonNode, jsonPath);

            // Assert
            result.Count().Should().Be(3);
            var list = result.ToList();
            list[0]!.GetValue<int>().Should().Be(1);
            list[1].Should().BeNull();
            list[2]!.GetValue<int>().Should().Be(3);
        }
    }

    [TestFixture]
    public class When_selecting_paths_and_coercing_to_string
    {
        private static IEnumerable<string> SelectNodesFromArrayPathCoerceToStrings(JsonNode jsonNode, string jsonPathString)
        {
            var logger = A.Fake<ILogger>();
            return jsonNode.SelectNodesFromArrayPathCoerceToStrings(jsonPathString, logger);
        }

        [Test]
        public void Given_array_containers_integers__Then_selected_values_are_returned_as_strings()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": 1 },
            { ""value"": 3 }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[*].value";

            // Act
            var result = SelectNodesFromArrayPathCoerceToStrings(jsonNode, jsonPath);

            // Assert
            result.Count().Should().Be(2);
            var list = result.ToList();
            list[0]!.Should().Be("1");
            list[1]!.Should().Be("3");
        }

        [Test]
        public void Given_array_containers_decimals__Then_selected_values_are_returned_as_strings()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": 1.0245 },
            { ""value"": 0.00000000003 }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[*].value";

            // Act
            var result = SelectNodesFromArrayPathCoerceToStrings(jsonNode, jsonPath);

            // Assert
            result.Count().Should().Be(2);
            var list = result.ToList();
            list[0]!.Should().Be("1.0245");
            list[1]!.Should().Be("0.00000000003");
        }

        [Test]
        public void Given_array_containers_Booleans__Then_selected_values_are_returned_as_strings()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": true },
            { ""value"": false }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[*].value";

            // Act
            var result = SelectNodesFromArrayPathCoerceToStrings(jsonNode, jsonPath);

            // Assert
            result.Count().Should().Be(2);
            var list = result.ToList();
            list[0]!.Should().Be("true");
            list[1]!.Should().Be("false");
        }

        [Test]
        public void Given_array_containers_an_object__Then_throw_exception()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": { ""nested"": ""object"" } },
            { ""value"": { ""nested"": ""second"" }  }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.items[*].value";

            // Act
            Action act = () => _ = SelectNodesFromArrayPathCoerceToStrings(jsonNode, jsonPath).ToList();

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class When_selecting_node_from_path_as_generic
    {
        private static T? SelectNodeFromPathAs<T>(JsonNode jsonNode, string jsonPathString)
        {
            var logger = A.Fake<ILogger>();
            return jsonNode.SelectNodeFromPathAs<T>(jsonPathString, logger);
        }

        [Test]
        public void Given_element_does_not_exist__Then_return_null()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": { ""nested"": ""object"" } }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectNodeFromPathAs<string?>(jsonNode, jsonPath);

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void Given_item_is_an_int__Then_return_an_int()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": 1 }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectNodeFromPathAs<int?>(jsonNode, jsonPath);

            // Assert
            result.Should().NotBeNull();
            result!.Value.Should().Be(1);
        }

        [Test]
        public void Given_item_is_a_decimal_Then_return_a_decimal()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": 1.0003 }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectNodeFromPathAs<double?>(jsonNode, jsonPath);

            // Assert
            result.Should().NotBeNull();
            result!.Value.Should().Be(1.0003);
        }

        [Test]
        public void Given_item_is_a_Boolean_Then_return_a_Boolean()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": true }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectNodeFromPathAs<bool?>(jsonNode, jsonPath);

            // Assert
            result.Should().NotBeNull();
            result!.Value.Should().Be(true);
        }

        [Test]
        public void Given_item_is_an_object__Then_throw_exception()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": { ""nested"": true } }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            Action act = () => _ = SelectNodeFromPathAs<bool?>(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Given_item_is_a_string_but_Boolean_requested__Then_throw_an_exception()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": ""string"" }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            Action act = () => SelectNodeFromPathAs<bool?>(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class When_selecting_a_required_node_from_path_as_string
    {
        private static string SelectRequiredNodeFromPathCoerceToString(JsonNode jsonNode, string jsonPathString)
        {
            var logger = A.Fake<ILogger>();
            return jsonNode.SelectRequiredNodeFromPathCoerceToString(jsonPathString, logger);
        }

        [Test]
        public void Given_element_does_not_exist__Then_throw_exception()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": { ""nested"": ""object"" } }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            Action act = () => SelectRequiredNodeFromPathCoerceToString(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Given_item_is_an_int__Then_return_an_int()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": 1 }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectRequiredNodeFromPathCoerceToString(jsonNode, jsonPath);

            // Assert
            result.Should().Be("1");
        }

        [Test]
        public void Given_item_is_a_decimal_Then_return_a_decimal()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": 1.0003 }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectRequiredNodeFromPathCoerceToString(jsonNode, jsonPath);

            // Assert
            result.Should().Be("1.0003");
        }

        [Test]
        public void Given_item_is_a_Boolean_Then_return_a_Boolean()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": true }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectRequiredNodeFromPathCoerceToString(jsonNode, jsonPath);

            // Assert
            result.Should().Be("true");
        }

        [Test]
        public void Given_item_is_an_object__Then_throw_exception()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": { ""nested"": true } }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            Action act = () => _ = SelectRequiredNodeFromPathCoerceToString(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [TestFixture]
    public class When_selecting_a_required_node_from_path_as_generic
    {
        private static T SelectRequiredNodeFromPathAs<T>(JsonNode jsonNode, string jsonPathString)
        {
            var logger = A.Fake<ILogger>();
            return jsonNode.SelectRequiredNodeFromPathAs<T>(jsonPathString, logger);
        }

        [Test]
        public void Given_element_does_not_exist__Then_throw_exception()
        {
            // Arrange
            string jsonString =
                @"{
        ""items"": [
            { ""value"": { ""nested"": ""object"" } }
        ]
    }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            Action act = () => SelectRequiredNodeFromPathAs<string>(jsonNode, jsonPath);

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void Given_integer_value__Then_return_integer()
        {
            // Arrange
            string jsonString =
                @"{ ""value"": 1 }";
            JsonNode jsonNode = JsonNode.Parse(jsonString)!;
            string jsonPath = "$.value";

            // Act
            var result = SelectRequiredNodeFromPathAs<int>(jsonNode, jsonPath);

            // Assert
            result.Should().Be(1);
        }
    }
}
