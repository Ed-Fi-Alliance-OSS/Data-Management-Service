// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// DMS-1563 public-boundary coverage for string query filters: a filter longer than its field's declared
/// maxLength, or on SQL Server wider than its column in UTF-16 code units, is answered as an invalid filter
/// value rather than run; and on SQL Server the generated filtered-read commands declare their string
/// parameters identically whatever the filter lengths, so their cached plans are reused.
/// </summary>
/// <remarks>
/// Runs against the AuthorizationQuery fixture with NamespaceBased read authorization, so every collection
/// read carries the namespace prefix authorization predicate and the seeded rows include ones the caller
/// is not authorized for.
/// </remarks>
internal static class StringQueryFilterScenario
{
    private const string Endpoint = NamespaceAuthorizationProblemDetailsScenario.NamespaceResourcesEndpoint;
    private const string GradeLevelDescriptorsEndpoint = "/data/ed-fi/gradeLevelDescriptors";
    private const int NameMaxLength = 75;
    private const int CodeValueMaxLength = 50;
    private const int PageLimit = 2;
    private const int CopiesPerCombination = 3;

    // One text element but two UTF-16 code units, so a run of them fits a field's maxLength in text elements
    // while being twice as wide in SQL Server storage units.
    private const string Emoji = "\U0001F600";

    private static readonly string[] _names = ["A", "Bravo", "Charlie Delta Echo"];

    private static readonly string[] _authorizedNamespaces =
    [
        $"{NamespaceAuthorizationProblemDetailsScenario.AuthorizedPrefix}a",
        $"{NamespaceAuthorizationProblemDetailsScenario.AuthorizedPrefix}a-considerably-longer-namespace",
        $"{NamespaceAuthorizationProblemDetailsScenario.SecondAuthorizedPrefix}bb",
    ];

    private const string UnauthorizedNamespace = "uri://other.example/cc";

    /// <summary>
    /// Repeated collection reads whose two string filters vary in length independently reuse the plans of the
    /// SQL Server batches the first read compiled: after that warm-up no new batch text appears for the
    /// resource, every cached batch declares each string parameter by its column contract, and each batch's
    /// statements keep their plan and count every measured execution. The reads still return the right
    /// authorized documents, pages and total counts.
    /// </summary>
    public static async Task It_reuses_the_cached_sql_server_plans_for_string_filters_of_varying_lengths(
        ApiIntegrationHarness harness
    )
    {
        await NamespaceAuthorizationProblemDetailsScenario.SeedReferenceDataAsync(harness);
        Dictionary<(string Name, string Namespace), List<int>> seededIds = await SeedCombinationsAsync(
            harness
        );

        // Warm-up compiles each batch once. Nothing below writes, changes statistics or alters the session.
        await AssertPageAsync(harness, seededIds, _names[0], _authorizedNamespaces[0], offset: 0);

        IReadOnlyList<CachedStatement> warm = await ReadCachedStatementsAsync(harness.DbConnection);
        warm.Should().NotBeEmpty("the warm-up read must have left its batches in the plan cache");

        int measuredReads = 0;

        foreach (string name in _names)
        {
            foreach (string @namespace in _authorizedNamespaces.Append(UnauthorizedNamespace))
            {
                foreach (int offset in new[] { 0, PageLimit })
                {
                    await AssertPageAsync(harness, seededIds, name, @namespace, offset);
                    measuredReads++;
                }
            }
        }

        IReadOnlyList<CachedStatement> measured = await ReadCachedStatementsAsync(harness.DbConnection);

        measured
            .Select(statement => statement.BatchText)
            .Distinct()
            .Should()
            .BeEquivalentTo(
                warm.Select(statement => statement.BatchText).Distinct(),
                "filter values of other lengths must not produce a differently declared batch"
            );

        foreach (string batchText in measured.Select(statement => statement.BatchText).Distinct())
        {
            batchText.Should().Contain($"@name nvarchar({NameMaxLength})");
            batchText.Should().Contain("@namespace nvarchar(255)");
            batchText.Should().Contain("nvarchar(max)", "namespace prefix patterns are declared unbounded");
        }

        foreach (CachedStatement before in warm)
        {
            CachedStatement after = measured.Single(statement => statement.Key == before.Key);

            after.PlanHandle.Should().Equal(before.PlanHandle, "the statement must keep its cached plan");
            after
                .PlanGenerationNumber.Should()
                .Be(
                    before.PlanGenerationNumber,
                    "nothing in the measured reads should recompile the statement after warm-up"
                );
            (after.ExecutionCount - before.ExecutionCount)
                .Should()
                .BeGreaterThanOrEqualTo(
                    measuredReads,
                    "every measured read reuses the warm-up batch, so each of its statements runs again"
                );
        }

        await TestContext.Out.WriteLineAsync(
            $"DMS-1563 plan reuse: {measuredReads} measured reads; "
                + $"{measured.Select(statement => statement.BatchText).Distinct().Count()} batch(es), "
                + $"{measured.Count} statement(s); "
                + string.Join(
                    " | ",
                    measured
                        .Select(statement => statement.BatchText)
                        .Distinct()
                        .Select(text => text[..Math.Min(text.Length, 400)].ReplaceLineEndings(" "))
                )
                + "; "
                + string.Join(
                    "; ",
                    measured.Select(statement =>
                        $"offset {statement.Key.StatementStartOffset}: executions "
                        + $"{warm.Single(w => w.Key == statement.Key).ExecutionCount}->{statement.ExecutionCount}, "
                        + $"plan_generation_num {warm.Single(w => w.Key == statement.Key).PlanGenerationNumber}->"
                        + $"{statement.PlanGenerationNumber}"
                    )
                )
        );
    }

    /// <summary>
    /// A name exactly as long as its declared maxLength is accepted and finds the stored document; one more
    /// character, including a trailing space, is rejected as an invalid filter value instead of being
    /// shortened into the stored value.
    /// </summary>
    public static async Task It_rejects_a_filter_over_the_declared_max_length_rather_than_matching_its_prefix(
        ApiIntegrationHarness harness
    )
    {
        await NamespaceAuthorizationProblemDetailsScenario.SeedReferenceDataAsync(harness);
        string storedName = new('n', NameMaxLength);
        await CreateAsync(harness, 1, storedName, _authorizedNamespaces[0]);

        using (
            HttpResponseMessage atMaxLength = await GetCollectionAsync(
                harness,
                Endpoint,
                ("name", storedName)
            )
        )
        {
            string body = await atMaxLength.Content.ReadAsStringAsync();
            atMaxLength.StatusCode.Should().Be(HttpStatusCode.OK, body);
            JsonNode
                .Parse(body)!
                .AsArray()
                .Select(item => item!["authorizationNamespaceId"]!.GetValue<int>())
                .Should()
                .Equal(1);
        }

        foreach (string overLong in new[] { storedName + "X", storedName + " " })
        {
            using HttpResponseMessage response = await GetCollectionAsync(
                harness,
                Endpoint,
                ("name", overLong)
            );
            await AssertInvalidFilterAsync(response, "$.name", overLong, "name");
        }
    }

    /// <summary>
    /// A name within its declared maxLength in text elements but wider than its SQL Server column in UTF-16
    /// code units is rejected on SQL Server, on both the collection and the partition operation. PostgreSQL
    /// columns are not bounded that way, so the same filter runs there and matches nothing.
    /// </summary>
    public static async Task It_answers_a_filter_wider_than_its_sql_server_column_by_dialect(
        ApiIntegrationHarness harness,
        bool sqlServer
    )
    {
        await NamespaceAuthorizationProblemDetailsScenario.SeedReferenceDataAsync(harness);
        string wideName = string.Concat(Enumerable.Repeat(Emoji, (NameMaxLength / 2) + 1));

        using HttpResponseMessage collection = await GetCollectionAsync(
            harness,
            Endpoint,
            ("name", wideName)
        );
        using HttpResponseMessage partitions = await GetCollectionAsync(
            harness,
            $"{Endpoint}/partitions",
            ("number", "2"),
            ("name", wideName)
        );

        if (sqlServer)
        {
            await AssertInvalidFilterAsync(collection, "$.name", wideName, "name");
            await AssertInvalidFilterAsync(partitions, "$.name", wideName, "name");
        }
        else
        {
            string collectionBody = await collection.Content.ReadAsStringAsync();
            collection.StatusCode.Should().Be(HttpStatusCode.OK, collectionBody);
            JsonNode.Parse(collectionBody)!.AsArray().Should().BeEmpty();
            partitions
                .StatusCode.Should()
                .Be(HttpStatusCode.OK, await partitions.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// Descriptor endpoint filters follow the same rules through the descriptor read path: over the declared
    /// maxLength is rejected everywhere, and wider than the <c>dms.Descriptor</c> column is rejected on
    /// SQL Server only.
    /// </summary>
    public static async Task It_answers_over_long_descriptor_filters_by_dialect(
        ApiIntegrationHarness harness,
        bool sqlServer
    )
    {
        string overMaxLength = new('c', CodeValueMaxLength + 1);
        using (
            HttpResponseMessage response = await GetCollectionAsync(
                harness,
                GradeLevelDescriptorsEndpoint,
                ("codeValue", overMaxLength)
            )
        )
        {
            await AssertInvalidFilterAsync(response, "$.codeValue", overMaxLength, "codeValue");
        }

        string wideCodeValue = string.Concat(Enumerable.Repeat(Emoji, (CodeValueMaxLength / 2) + 1));
        using HttpResponseMessage wide = await GetCollectionAsync(
            harness,
            GradeLevelDescriptorsEndpoint,
            ("codeValue", wideCodeValue)
        );

        if (sqlServer)
        {
            await AssertInvalidFilterAsync(wide, "$.codeValue", wideCodeValue, "codeValue");
        }
        else
        {
            string body = await wide.Content.ReadAsStringAsync();
            wide.StatusCode.Should().Be(HttpStatusCode.OK, body);
            JsonNode.Parse(body)!.AsArray().Should().BeEmpty();
        }
    }

    private static async Task<Dictionary<(string Name, string Namespace), List<int>>> SeedCombinationsAsync(
        ApiIntegrationHarness harness
    )
    {
        Dictionary<(string Name, string Namespace), List<int>> seededIds = [];
        int nextId = 1;

        foreach (string name in _names)
        {
            foreach (string @namespace in _authorizedNamespaces.Append(UnauthorizedNamespace))
            {
                List<int> ids = [];

                for (int copy = 0; copy < CopiesPerCombination; copy++)
                {
                    await CreateAsync(harness, nextId, name, @namespace);
                    ids.Add(nextId++);
                }

                seededIds[(name, @namespace)] = ids;
            }
        }

        return seededIds;
    }

    private static async Task CreateAsync(
        ApiIntegrationHarness harness,
        int authorizationNamespaceId,
        string name,
        string @namespace
    )
    {
        using HttpResponseMessage response = await NamespaceAuthorizationProblemDetailsScenario.PostJsonAsync(
            harness,
            Endpoint,
            new JsonObject
            {
                ["authorizationNamespaceId"] = authorizationNamespaceId,
                ["name"] = name,
                ["namespace"] = @namespace,
                ["schoolReference"] = new JsonObject
                {
                    ["schoolId"] = NamespaceAuthorizationProblemDetailsScenario.SchoolId,
                },
                ["classPeriods"] = new JsonArray(),
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Reads one page filtered by both strings and checks it against the seed: an authorized combination
    /// pages through its three documents with a total count of three, and the unauthorized namespace yields
    /// nothing although the same name is stored there.
    /// </summary>
    private static async Task AssertPageAsync(
        ApiIntegrationHarness harness,
        Dictionary<(string Name, string Namespace), List<int>> seededIds,
        string name,
        string @namespace,
        int offset
    )
    {
        using HttpResponseMessage response = await GetCollectionAsync(
            harness,
            Endpoint,
            ("name", name),
            ("namespace", @namespace),
            ("limit", PageLimit.ToString()),
            ("offset", offset.ToString()),
            ("totalCount", "true")
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        int[] returnedIds =
        [
            .. JsonNode
                .Parse(body)!
                .AsArray()
                .Select(item => item!["authorizationNamespaceId"]!.GetValue<int>()),
        ];
        int[] expectedIds =
            @namespace == UnauthorizedNamespace
                ? []
                : [.. seededIds[(name, @namespace)].Order().Skip(offset).Take(PageLimit)];
        int expectedTotal = @namespace == UnauthorizedNamespace ? 0 : CopiesPerCombination;

        returnedIds
            .Order()
            .Should()
            .Equal(expectedIds, $"name '{name}', namespace '{@namespace}', offset {offset}");
        response.Headers.GetValues("Total-Count").Single().Should().Be(expectedTotal.ToString());
    }

    private static Task<HttpResponseMessage> GetCollectionAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        params (string Name, string Value)[] query
    ) =>
        harness.HttpClient.GetAsync(
            $"{endpoint}?{string.Join("&", query.Select(term => $"{term.Name}={Uri.EscapeDataString(term.Value)}"))}"
        );

    private static async Task AssertInvalidFilterAsync(
        HttpResponseMessage response,
        string documentPath,
        string value,
        string queryFieldName
    )
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request:data-validation-failed");
        problem["validationErrors"]![documentPath]!
            .AsArray()
            .Select(message => message!.GetValue<string>())
            .Should()
            .Equal($"The value '{value}' is not valid for {queryFieldName}.");
    }

    private sealed record CachedStatementKey(string BatchText, int StatementStartOffset);

    private sealed record CachedStatement(
        CachedStatementKey Key,
        byte[] PlanHandle,
        long ExecutionCount,
        long PlanGenerationNumber
    )
    {
        public string BatchText => Key.BatchText;
    }

    /// <summary>
    /// The cached statements of the batches DMS compiled in the leased database that filter the resource by
    /// <c>@name</c>. The plan's <c>dbid</c> attribute scopes the read to this test's database, and the
    /// parameter name scopes it to the filtered reads, so no other test's queries can satisfy an assertion.
    /// </summary>
    private static async Task<IReadOnlyList<CachedStatement>> ReadCachedStatementsAsync(
        DbConnection connection
    )
    {
        await using DbCommand command = connection.CreateCommand();
        // The plan cache of a shared server holds thousands of statements from large batches, so each batch
        // text is read once per handle and matched under a binary collation before any plan attribute is
        // read; a statement-by-statement collation-aware LIKE over that cache does not finish in time.
        command.CommandTimeout = 120;
        command.CommandText = """
            SELECT DISTINCT qs.sql_handle
            INTO #handles
            FROM sys.dm_exec_query_stats AS qs;

            SELECT h.sql_handle, st.[text]
            INTO #batches
            FROM #handles AS h
            CROSS APPLY sys.dm_exec_sql_text(h.sql_handle) AS st
            WHERE st.[text] COLLATE Latin1_General_BIN2 LIKE N'%AuthorizationNamespaceResource%'
                AND st.[text] COLLATE Latin1_General_BIN2 LIKE N'%@name nvarchar%'
                AND st.[text] COLLATE Latin1_General_BIN2 NOT LIKE N'%dm_exec_query_stats%';

            SELECT b.[text], qs.statement_start_offset, qs.plan_handle, qs.execution_count, qs.plan_generation_num
            FROM #batches AS b
            JOIN sys.dm_exec_query_stats AS qs ON qs.sql_handle = b.sql_handle
            CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
            WHERE pa.attribute = N'dbid' AND CONVERT(int, pa.[value]) = DB_ID();

            DROP TABLE #batches;
            DROP TABLE #handles;
            """;

        List<CachedStatement> statements = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            statements.Add(
                new CachedStatement(
                    new CachedStatementKey(reader.GetString(0), reader.GetInt32(1)),
                    (byte[])reader.GetValue(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)
                )
            );
        }

        return statements;
    }
}
