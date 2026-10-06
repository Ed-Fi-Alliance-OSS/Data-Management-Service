// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

/// <summary>
/// Scans both engines' repository sources for every place a data store or derivative response is
/// built, and asserts each one takes its connection string from the read seam with the right row
/// context. The sites are found from the source rather than listed by line, so a new projection that
/// bypasses the seam, or an existing one that stops using it, fails here.
/// </summary>
[TestFixture]
public partial class Given_the_repository_sources_of_both_engines
{
    private const string ReadCall = "ConnectionString = await connectionStringReader.ReadAsync(";

    private static readonly string[] _engines = ["Postgresql", "Mssql"];

    private sealed record Site(string Engine, string Method, string Row);

    private readonly List<Site> _sites = [];
    private readonly List<string> _bypasses = [];
    private readonly List<string> _base64OverConnectionStrings = [];

    [GeneratedRegex(@"new (DataStoreResponse|DataStoreDerivativeResponse)\b")]
    private static partial Regex ResponseConstruction();

    [GeneratedRegex(@"public async Task<\w+> (\w+)\(")]
    private static partial Regex MethodDeclaration();

    [GeneratedRegex(@"Convert\.ToBase64String\([^;]*ConnectionString", RegexOptions.Singleline)]
    private static partial Regex Base64OverConnectionString();

    [GeneratedRegex(@"\G\s*row\.ConnectionString|\G\s*result\.Value\.ConnectionString")]
    private static partial Regex StoredColumnArgument();

    [GeneratedRegex(@"new ConnectionStringRow\.DataStore\(|DerivativeReadMode\.\w+")]
    private static partial Regex RowContext();

    [OneTimeSetUp]
    public void Scan()
    {
        string root = Given_E2E_Test_Fragments.FindRepositoryRoot();

        foreach (string engine in _engines)
        {
            string directory = Path.Combine(
                root,
                "src",
                "config",
                "backend",
                $"EdFi.DmsConfigurationService.Backend.{engine}",
                "Repositories"
            );

            foreach (string file in Directory.GetFiles(directory, "*.cs").Order(StringComparer.Ordinal))
            {
                string source = File.ReadAllText(file);
                string name = Path.GetFileName(file);

                if (Base64OverConnectionString().IsMatch(source))
                {
                    _base64OverConnectionStrings.Add($"{engine}/{name}");
                }

                foreach (Match construction in ResponseConstruction().Matches(source))
                {
                    MatchCollection declarations = MethodDeclaration().Matches(source[..construction.Index]);
                    string method = declarations[declarations.Count - 1].Groups[1].Value;
                    string initializer = Initializer(source, construction.Index);
                    int read = initializer.IndexOf(ReadCall, StringComparison.Ordinal);

                    if (read < 0)
                    {
                        _bypasses.Add($"{engine}/{name} {method}");
                        continue;
                    }

                    // The seam has to be handed the column the row was read with, not some other value.
                    if (!StoredColumnArgument().IsMatch(initializer, read + ReadCall.Length))
                    {
                        _bypasses.Add($"{engine}/{name} {method} reads something other than the column");
                        continue;
                    }

                    string row = RowContext().Match(initializer, read).Value;
                    _sites.Add(
                        new Site(
                            engine,
                            method,
                            row.StartsWith("new ", StringComparison.Ordinal) ? "DataStore" : row
                        )
                    );
                }
            }
        }
    }

    /// <summary>The object initializer following a construction, by brace matching.</summary>
    private static string Initializer(string source, int from)
    {
        int open = source.IndexOf('{', from);
        int depth = 0;

        for (int i = open; i < source.Length; i++)
        {
            depth += source[i] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced initializer.");
    }

    [Test]
    public void It_finds_no_base64_over_a_connection_string_column() =>
        _base64OverConnectionStrings.Should().BeEmpty();

    [Test]
    public void It_finds_no_response_built_without_the_seam() => _bypasses.Should().BeEmpty();

    [Test]
    public void It_finds_every_projection_site_reading_through_the_seam_with_its_row_context() =>
        _sites
            .Should()
            .BeEquivalentTo(
                _engines.SelectMany(engine =>
                    new[]
                    {
                        new Site(engine, "QueryDataStore", "DataStore"),
                        new Site(engine, "GetDataStore", "DataStore"),
                        new Site(engine, "QueryDataStoreDerivative", "DerivativeReadMode.Resource"),
                        new Site(engine, "GetDataStoreDerivative", "DerivativeReadMode.Resource"),
                        new Site(
                            engine,
                            "GetDataStoreDerivativesByDataStore",
                            "DerivativeReadMode.PartOfDataStore"
                        ),
                        new Site(
                            engine,
                            "GetDataStoreDerivativesByDataStoreIds",
                            "DerivativeReadMode.PartOfDataStore"
                        ),
                    }
                )
            );
}
