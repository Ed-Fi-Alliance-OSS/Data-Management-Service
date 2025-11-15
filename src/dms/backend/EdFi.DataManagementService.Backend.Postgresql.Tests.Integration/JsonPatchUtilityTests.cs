// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class JsonPatchUtilityTests
{
    [Test]
    public void ComputePatch_returns_null_when_documents_are_identical()
    {
        const string json = """{"abc":1,"nested":{"value":10}}""";

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement existing = doc.RootElement;
        JsonNode newNode = JsonNode.Parse(json)!;

        JsonNode? patch = JsonPatchUtility.ComputePatch(existing, newNode);

        patch.Should().BeNull();
    }

    [Test]
    public void ComputePatch_returns_patch_with_supported_ops_when_value_changes()
    {
        const string existingJson = """{"abc":1}""";
        const string newJson = """{"abc":2}""";

        using JsonDocument existingDoc = JsonDocument.Parse(existingJson);
        JsonElement existing = existingDoc.RootElement;
        JsonNode newNode = JsonNode.Parse(newJson)!;

        JsonNode? patch = JsonPatchUtility.ComputePatch(existing, newNode);

        patch.Should().NotBeNull();
        JsonPatchUtility.HasOnlySupportedOps(patch!).Should().BeTrue();
    }

    [Test]
    public void HasOnlySupportedOps_returns_false_for_unsupported_op()
    {
        JsonArray patch =
        [
            new JsonObject
            {
                ["op"] = "move",
                ["path"] = "/abc",
                ["from"] = "/xyz",
            },
        ];

        JsonPatchUtility.HasOnlySupportedOps(patch).Should().BeFalse();
    }
}
