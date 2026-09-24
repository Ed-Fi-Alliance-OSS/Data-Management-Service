// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

// Shared by CDC documentation tests; never executes Markdown.
internal static class CdcRunbookExamples
{
    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory is not null)
            {
                if (
                    File.Exists(
                        Path.Combine(directory.FullName, "reference/cdc-documentation/operations-runbook.md")
                    )
                )
                {
                    return directory.FullName;
                }
                directory = directory.Parent!;
            }
            throw new InvalidOperationException("CDC runbook requires a repository checkout.");
        }
    }

    internal static string Markdown =>
        File.ReadAllText(Path.Combine(RepositoryRoot, "reference/cdc-documentation/operations-runbook.md"));

    internal static string Read(string id, string language = "powershell") => Extract(Markdown, id, language);

    internal static string Extract(string markdown, string id, string language)
    {
        string start = $"<!-- cdc-snippet: {id} -->";
        string end = $"<!-- /cdc-snippet: {id} -->";
        Regex
            .Matches(markdown, Regex.Escape(start))
            .Count.Should()
            .Be(1, $"snippet {id} must occur exactly once");
        Regex
            .Matches(markdown, Regex.Escape(end))
            .Count.Should()
            .Be(1, $"snippet {id} must have exactly one end marker");
        var match = Regex.Match(
            markdown,
            Regex.Escape(start) + @"\s*```" + language + @"\r?\n(?<code>.*?)\r?\n```\s*" + Regex.Escape(end),
            RegexOptions.Singleline
        );
        match.Success.Should().BeTrue($"snippet {id} must contain only one {language} fence");
        match.Groups["code"].Value.Should().NotContain("```", "nested/unrelated blocks must never execute");
        return match.Groups["code"].Value;
    }

    internal static void AssertExcerpt(string id, string actual, params string[] paths) =>
        AssertExcerptJson(Read(id, "json"), actual, paths);

    internal static void AssertExcerptJson(string example, string actual, params string[] paths)
    {
        var source = JsonNode.Parse(actual)!;
        var projected = new JsonObject();
        foreach (string path in paths)
        {
            CopyPath(source, projected, path.Split('/'), 0);
        }
        JsonNode
            .DeepEquals(JsonNode.Parse(example), projected)
            .Should()
            .BeTrue(
                $"the documented excerpt must match production serialization: {projected.ToJsonString()}"
            );
    }

    // Numeric path segments address the fixture's first target/diagnostic only.
    // The selected paths are test-owned, so dropping a required example field fails too.
    private static void CopyPath(JsonNode source, JsonNode destination, string[] path, int index)
    {
        string key = path[index];
        JsonNode value = source is JsonArray array ? array[int.Parse(key)]! : source[key]!;
        if (value is null)
        {
            return; // Production omits optional null fields; the example must omit them as well.
        }
        if (index == path.Length - 1)
        {
            if (destination is JsonArray targetArray)
            {
                targetArray.Add(value.DeepClone());
            }
            else
            {
                destination[key] = value.DeepClone();
            }
            return;
        }
        JsonNode child;
        if (destination is JsonArray items)
        {
            if (items.Count == 0)
            {
                items.Add(new JsonObject());
            }
            child = items[int.Parse(key)]!;
        }
        else
        {
            destination[key] ??= int.TryParse(path[index + 1], out _) ? new JsonArray() : new JsonObject();
            child = destination[key]!;
        }
        CopyPath(value, child, path, index + 1);
    }
}
