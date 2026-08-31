// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCache")]
public sealed class Given_The_DocumentCacheEvidenceMatrix
{
    private static readonly string[] InScopeContractIds =
    [
        "CDC-INV-01",
        "CDC-INV-02",
        "CDC-INV-03",
        "CDC-INV-04",
        "CDC-INV-05",
        "CDC-INV-14",
        "CDC-INV-15",
    ];

    private static readonly Regex RepositoryPathPattern = new(
        @"`(?<path>(?:src|docs|reference)/[^`\r\n]+\.(?:cs|csproj|json|md|ps1))`",
        RegexOptions.Compiled
    );
    private static readonly Regex CodeSpanPattern = new(@"`(?<value>[^`\r\n]+)`", RegexOptions.Compiled);
    private static readonly Regex TestMethodNamePattern = new(
        @"^(?:It_|[A-Za-z][A-Za-z0-9]*_it_)[A-Za-z0-9_]*$",
        RegexOptions.Compiled
    );

    private string _matrix = null!;

    [SetUp]
    public async Task SetUp()
    {
        _matrix = await File.ReadAllTextAsync(MatrixPath());
    }

    [Test]
    public void It_documents_each_in_scope_contract_row_once()
    {
        IReadOnlyList<string> rows = MatrixRows();

        foreach (string contractId in InScopeContractIds)
        {
            rows.Where(row => row.Contains($"| `{contractId}` |", StringComparison.Ordinal))
                .Should()
                .ContainSingle($"DMS-1317 needs one matrix row for {contractId}");
        }
    }

    [Test]
    public void It_references_only_existing_repository_files()
    {
        string repositoryRoot = RepositoryRoot();

        List<string> missingPaths = ReferencedRepositoryPaths()
            .Where(path => !File.Exists(Path.Combine(repositoryRoot, path)))
            .Order()
            .ToList();

        missingPaths.Should().BeEmpty("the CDC-INV matrix should not point at stale test evidence");
    }

    [Test]
    public void It_labels_external_scope_boundaries()
    {
        _matrix.Should().Contain("CDC-INV-06");
        _matrix.Should().Contain("CDC-INV-13");
        _matrix.Should().Contain("E19-owned");
        _matrix.Should().Contain("DMS-1318");
        _matrix.Should().Contain("restamp");
        _matrix.Should().Contain("conditional DMS-1317 evidence");
    }

    [Test]
    public void It_keeps_each_in_scope_row_tied_to_concrete_test_evidence()
    {
        foreach (string row in MatrixRows())
        {
            row.Should().Contain("`src/dms/", "each contract row should cite concrete test evidence");
        }
    }

    [Test]
    public void It_resolves_named_test_evidence_to_declared_methods()
    {
        string repositoryRoot = RepositoryRoot();

        List<string> unresolvedReferences = ReferencedTestMethodEvidence()
            .Where(reference =>
                reference.RepositoryPath.Length == 0
                || !TestMethodExists(
                    Path.Combine(repositoryRoot, reference.RepositoryPath),
                    reference.MethodName
                )
            )
            .Select(reference =>
                reference.RepositoryPath.Length == 0
                    ? $"{reference.ContractId}: {reference.MethodName} is not tied to a repository .cs path"
                    : $"{reference.RepositoryPath}: {reference.MethodName}"
            )
            .Order()
            .ToList();

        unresolvedReferences
            .Should()
            .BeEmpty("named test evidence should stay anchored to declared test methods");
    }

    private static string MatrixPath() =>
        Path.Combine(RepositoryRoot(), "reference", "document-cache", "cdc-inv-evidence.md");

    private static string RepositoryRoot()
    {
        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            string solutionPath = Path.Combine(
                currentDirectory.FullName,
                "src",
                "dms",
                "EdFi.DataManagementService.sln"
            );

            if (File.Exists(solutionPath))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private IReadOnlyList<string> MatrixRows() =>
        _matrix
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line =>
                Array.Exists(
                    InScopeContractIds,
                    contractId => line.Contains($"| `{contractId}` |", StringComparison.Ordinal)
                )
            )
            .ToList();

    private IEnumerable<string> ReferencedRepositoryPaths() =>
        RepositoryPathPattern
            .Matches(_matrix)
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal);

    private IEnumerable<TestMethodReference> ReferencedTestMethodEvidence()
    {
        foreach (string row in MatrixRows())
        {
            string contractId = ContractIdFromRow(row);
            string[] cells = MarkdownCells(row);

            if (cells.Length < 6)
            {
                continue;
            }

            List<string> currentCSharpRepositoryPaths = [];

            foreach (string evidenceClause in cells[5].Split(';', StringSplitOptions.TrimEntries))
            {
                string[] codeSpanValues = CodeSpanPattern
                    .Matches(evidenceClause)
                    .Select(match => match.Groups["value"].Value)
                    .ToArray();

                string[] repositoryPaths = codeSpanValues.Where(IsRepositoryPath).ToArray();
                if (repositoryPaths.Length > 0)
                {
                    currentCSharpRepositoryPaths = repositoryPaths
                        .Where(path => path.EndsWith(".cs", StringComparison.Ordinal))
                        .ToList();
                }

                foreach (
                    string testMethodName in codeSpanValues.Where(value =>
                        TestMethodNamePattern.IsMatch(value)
                    )
                )
                {
                    if (currentCSharpRepositoryPaths.Count == 0)
                    {
                        yield return new TestMethodReference(contractId, "", testMethodName);
                        continue;
                    }

                    foreach (string repositoryPath in currentCSharpRepositoryPaths)
                    {
                        yield return new TestMethodReference(contractId, repositoryPath, testMethodName);
                    }
                }
            }
        }
    }

    private static bool TestMethodExists(string fullPath, string methodName)
    {
        if (!File.Exists(fullPath))
        {
            return false;
        }

        string source = File.ReadAllText(fullPath);

        return Regex.IsMatch(
            source,
            $@"\b(?:public|internal|private|protected)\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask|void)\s+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.CultureInvariant
        );
    }

    private static string[] MarkdownCells(string row) =>
        row.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static string ContractIdFromRow(string row) =>
        InScopeContractIds.Single(contractId =>
            row.Contains($"| `{contractId}` |", StringComparison.Ordinal)
        );

    private static bool IsRepositoryPath(string value) =>
        value.StartsWith("src/", StringComparison.Ordinal)
        || value.StartsWith("docs/", StringComparison.Ordinal)
        || value.StartsWith("reference/", StringComparison.Ordinal);

    private sealed record TestMethodReference(string ContractId, string RepositoryPath, string MethodName);
}
