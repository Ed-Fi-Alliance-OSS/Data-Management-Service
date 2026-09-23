// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_Cdc_runbook_links
{
    private static readonly string[] Documents =
    [
        "reference/cdc-documentation/README.md",
        "reference/cdc-documentation/operations-runbook.md",
        "reference/cdc-documentation/cdc-inv-evidence.md",
        "reference/document-cache-documentation/README.md",
        "reference/document-cache-documentation/operations-runbook.md",
        "src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md",
        "src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md",
        "eng/docker-compose/README.md",
        "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md",
        "docs/CONFIGURATION.md",
        "docs/RELATIONAL-BACKEND.md",
        "src/dms/tests/RestClient/local-development-setup.http",
    ];

    [TestCaseSource(nameof(Documents))]
    public void It_resolves_relative_links_and_explicit_or_generated_anchors(string relative)
    {
        string path = Path.Combine(CdcRunbookExamples.RepositoryRoot, relative);
        var failures = CheckLinks(path, File.ReadAllText(path));
        failures.Should().BeEmpty(relative);
    }

    [TestCase("[owner](operations-runbook.md#missing-cdc-anchor)")]
    [TestCase("[owner](missing-cdc-document.md)")]
    public void It_rejects_a_broken_copy(string markdown)
    {
        string path = Path.Combine(CdcRunbookExamples.RepositoryRoot, Documents[0]);
        CheckLinks(path, markdown).Should().ContainSingle();
    }

    [Test]
    public void It_rejects_a_broken_anchor_in_a_copied_operator_document()
    {
        string path = Path.Combine(CdcRunbookExamples.RepositoryRoot, Documents[0]);
        string original = File.ReadAllText(path);
        string copy = original.Replace(
            "operations-runbook.md#projection-handoff",
            "operations-runbook.md#broken-history-handoff",
            StringComparison.Ordinal
        );
        copy.Should().NotBe(original);
        CheckLinks(path, copy)
            .Should()
            .Contain(f => f.Contains("#broken-history-handoff", StringComparison.Ordinal));
    }

    [Test]
    public void It_handles_repeated_headings_explicit_anchors_and_ignores_fenced_examples()
    {
        Anchors("# A `name` / value\n# A `name` / value\n<a id=\"kept\"></a>\n```md\n# fake\n```\n")
            .Should()
            .BeEquivalentTo("a-name--value", "a-name--value-1", "kept");
    }

    internal static List<string> CheckLinks(string sourcePath, string markdown)
    {
        List<string> failures = [];
        string prose = WithoutFences(markdown);
        // These scoped documents use inline links. Reference definitions are checked too.
        var links = Regex.Matches(
            prose,
            @"\[[^\]\r\n]*\]\((?<url>[^\s)]+)(?:\s+""[^""]*"")?\)|(?m)^\s*\[[^\]]+\]:\s*(?<url>\S+)"
        );
        foreach (Match link in links)
        {
            string url = link.Groups["url"].Value.Trim('<', '>');
            if (
                Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")
                || url.StartsWith("//", StringComparison.Ordinal)
            )
            {
                continue;
            }
            string[] parts = url.Split('#', 2);
            string target =
                parts[0].Length == 0
                    ? sourcePath
                    : Path.GetFullPath(
                        Path.Combine(Path.GetDirectoryName(sourcePath)!, Uri.UnescapeDataString(parts[0]))
                    );
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                failures.Add($"{url}: missing path");
                continue;
            }
            if (
                parts.Length == 2
                && parts[1].Length > 0
                && target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !Anchors(File.ReadAllText(target)).Contains(Uri.UnescapeDataString(parts[1]))
            )
            {
                failures.Add($"{url}: missing anchor");
            }
        }
        return failures;
    }

    private static string WithoutFences(string markdown) =>
        Regex.Replace(markdown, @"(?ms)^\s*(`{3,}|~{3,})[^\r\n]*\r?\n.*?^\s*\1\s*$", "");

    private static HashSet<string> Anchors(string markdown)
    {
        string prose = WithoutFences(markdown);
        HashSet<string> anchors = [];
        foreach (Match match in Regex.Matches(prose, "<a\\s+(?:id|name)=[\"']([^\"']+)[\"']"))
        {
            anchors.Add(match.Groups[1].Value);
        }
        Dictionary<string, int> counts = [];
        foreach (Match match in Regex.Matches(prose, @"(?m)^ {0,3}#{1,6}\s+(.+?)(?:\s+#+)?\s*$"))
        {
            string heading = Regex.Replace(match.Groups[1].Value, @"\[([^\]]+)\]\([^)]*\)", "$1");
            heading = WebUtility.HtmlDecode(Regex.Replace(heading, "<[^>]+>", "")).ToLowerInvariant();
            string slug = Regex.Replace(heading, @"[^\p{L}\p{N}\p{M}\s_-]", "").Replace(' ', '-');
            int count = counts.GetValueOrDefault(slug);
            counts[slug] = count + 1;
            anchors.Add(count == 0 ? slug : $"{slug}-{count}");
        }
        return anchors;
    }
}
