// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class JsonComparer
{
    private static TestLogger _logger = new();
    public static JsonNode OrderJsonProperties(JsonNode jsonNode)
    {
        switch (jsonNode)
        {
            case JsonObject jsonObject:
                var orderedObject = new JsonObject();
                foreach (var property in jsonObject.OrderBy(p => p.Key))
                {
                    if (property.Value != null)
                    {
                        orderedObject[property.Key] = OrderJsonProperties(property.Value);
                    }
                }
                return orderedObject;

            case JsonArray jsonArray:
                var orderedArray = new JsonArray();
                foreach (var item in jsonArray)
                {
                    if (item != null)
                    {
                        orderedArray.Add(OrderJsonProperties(item));
                    }
                }
                return orderedArray;

            default:
                return jsonNode.DeepClone();
        }
    }

    public class JsonElementEqualityComparer : IEqualityComparer<JsonElement>
    {
        public static readonly JsonElementEqualityComparer Instance = new();

        public bool Equals(JsonElement expected, JsonElement response)
        {
            if (expected.ValueKind != response.ValueKind)
            {
                return false;
            }

            switch (expected.ValueKind)
            {
                case JsonValueKind.Object:
                    return AreObjectsEqual(expected, response);
                case JsonValueKind.Array:
                    return AreArraysEqual(expected, response);
                default:
                    var result = expected.ToString() == response.ToString();
                    if (!result)
                    {
                        _logger.log.Information("Difference: " + expected.ToString());
                    }

                    return result;
            }
        }

        public int GetHashCode(JsonElement obj)
        {
            throw new NotImplementedException();
        }

        private static bool AreObjectsEqual(JsonElement expected, JsonElement response)
        {
            var expectedProperties = expected.EnumerateObject().OrderBy(p => p.Name);
            var responseProperties = response.EnumerateObject().OrderBy(p => p.Name);

            var result = expectedProperties.SequenceEqual(responseProperties, PropertyEqualityComparer.Compare);
            return result;
        }

        private static bool AreArraysEqual(JsonElement expected, JsonElement response)
        {
            var expectedItems = expected.EnumerateArray().ToArray();
            var responseItems = response.EnumerateArray().ToArray();

            var result = expectedItems.SequenceEqual(responseItems, Instance);
            return result;
        }

        private class PropertyEqualityComparer : IEqualityComparer<JsonProperty>
        {
            internal static PropertyEqualityComparer Compare { get; } = new();

            public bool Equals(JsonProperty expected, JsonProperty response)
            {
                var result = expected.Name == response.Name && Instance.Equals(expected.Value, response.Value);
                return result;
            }

            public int GetHashCode(JsonProperty obj)
            {
                throw new NotImplementedException();
            }
        }
    }
}
