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
            .Split(Environment.NewLine)
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
}
