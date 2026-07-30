// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheLifecycleReaderSupportTests
{
    [TestCaseSource(nameof(ExactLifecycleTokens))]
    public async Task It_should_accept_exact_lifecycle_tokens(
        SqlDialect dialect,
        string lifecycleText,
        DocumentCacheLifecycleState expectedLifecycle
    )
    {
        DocumentCacheLifecycleReadResult result = await ReadLifecycleAsync(dialect, lifecycleText);

        result.Status.Should().Be(DocumentCacheLifecycleReadStatus.Succeeded);
        result.Lifecycle!.State.Should().Be(expectedLifecycle);
        result.Lifecycle.CacheAheadRecoveryRequired.Should().BeFalse();
    }

    [TestCaseSource(nameof(NonTokenLifecycleValues))]
    public async Task It_should_reject_non_token_lifecycle_values(SqlDialect dialect, string lifecycleText)
    {
        DocumentCacheLifecycleReadResult result = await ReadLifecycleAsync(dialect, lifecycleText);

        result.Status.Should().Be(DocumentCacheLifecycleReadStatus.Invalid);
        result.Lifecycle.Should().BeNull();
    }

    private static IEnumerable<TestCaseData> ExactLifecycleTokens()
    {
        SqlDialect[] dialects = [SqlDialect.Pgsql, SqlDialect.Mssql];

        foreach (SqlDialect dialect in dialects)
        {
            foreach (DocumentCacheLifecycleState lifecycle in Enum.GetValues<DocumentCacheLifecycleState>())
            {
                yield return new TestCaseData(dialect, lifecycle.ToString(), lifecycle).SetName(
                    $"{dialect}_{lifecycle}_is_accepted"
                );
            }
        }
    }

    private static IEnumerable<TestCaseData> NonTokenLifecycleValues()
    {
        SqlDialect[] dialects = [SqlDialect.Pgsql, SqlDialect.Mssql];
        string[] invalidValues = ["0", "99", "disabled", "Tracking ", "", "Paused"];

        foreach (SqlDialect dialect in dialects)
        {
            foreach (string invalidValue in invalidValues)
            {
                yield return new TestCaseData(dialect, invalidValue).SetName(
                    $"{dialect}_lifecycle_value_{Display(invalidValue)}_is_invalid"
                );
            }
        }
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        SqlDialect dialect,
        string lifecycleText
    )
    {
        DocumentCacheLifecycleReaderQuery query = DocumentCacheLifecycleReaderSupport.GetQuery(dialect);
        var command = new RecordingDbCommand(CreateReader(query, lifecycleText)) { ScalarResult = 1 };

        return await DocumentCacheLifecycleReaderSupport.ReadLifecycleAsync(
            () => new RecordingDbConnection(command),
            query,
            NullLogger.Instance
        );
    }

    private static DataTableReader CreateReader(DocumentCacheLifecycleReaderQuery query, string lifecycleText)
    {
        var table = new DataTable();
        table.Columns.Add(query.LifecycleColumnName, typeof(string));
        table.Columns.Add(query.CacheAheadRecoveryRequiredColumnName, typeof(bool));

        DataRow row = table.NewRow();
        row[query.LifecycleColumnName] = lifecycleText;
        row[query.CacheAheadRecoveryRequiredColumnName] = false;
        table.Rows.Add(row);

        return table.CreateDataReader();
    }

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "blank" : value.Trim();
}
