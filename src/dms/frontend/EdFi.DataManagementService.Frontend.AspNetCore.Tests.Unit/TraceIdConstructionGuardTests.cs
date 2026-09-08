// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_TraceId_Construction_Guardrails
{
    private const string AllowedNormalizeFile =
        "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs";
    private const string AllowedNoFile = "src/dms/core/EdFi.DataManagementService.Core/Model/No.cs";

    private static readonly Regex RawTraceIdConstructorPattern = new(@"new\s+TraceId\(");
    private static readonly Regex TargetTypedTraceIdConstructorPattern = new(
        @"\bTraceId\s+[A-Za-z_][A-Za-z0-9_]*\s*=\s*new\("
    );

    [Test]
    public void It_keeps_raw_trace_id_construction_inside_the_single_normalization_function()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dmsRoot = Path.Combine(repositoryRoot, "src", "dms");

        List<string> violations = [];

        foreach (string file in Directory.GetFiles(dmsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');

            if (relativePath.Contains("/Tests/") || relativePath.Contains(".Tests."))
            {
                continue;
            }

            if (relativePath == AllowedNoFile)
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);

            if (relativePath == AllowedNormalizeFile)
            {
                string[] rawConstructorMatches =
                [
                    .. FindMatchingLines(relativePath, lines, RawTraceIdConstructorPattern),
                ];

                rawConstructorMatches.Should().ContainSingle();
                rawConstructorMatches[0]
                    .Should()
                    .Contain(
                        "return new TraceId(LoggingSanitizer.SanitizeForCorrelationId(truncatedTraceId));"
                    );

                FindMatchingLines(relativePath, lines, TargetTypedTraceIdConstructorPattern)
                    .Should()
                    .BeEmpty();

                continue;
            }

            violations.AddRange(FindMatchingLines(relativePath, lines, RawTraceIdConstructorPattern));
            violations.AddRange(FindMatchingLines(relativePath, lines, TargetTypedTraceIdConstructorPattern));
        }

        violations
            .Should()
            .BeEmpty(
                "all production TraceId construction should route through AspNetCoreFrontend.NormalizeTraceId; unexpected matches: {0}",
                string.Join(Environment.NewLine, violations)
            );
    }

    private static IEnumerable<string> FindMatchingLines(string relativePath, string[] lines, Regex pattern)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            if (pattern.IsMatch(lines[index]))
            {
                yield return $"{relativePath}:{index + 1}: {lines[index].Trim()}";
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (
                File.Exists(Path.Combine(directory.FullName, "build-dms.ps1"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "dms"))
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
