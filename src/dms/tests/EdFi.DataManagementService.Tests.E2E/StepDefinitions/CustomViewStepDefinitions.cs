// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;
using Npgsql;
using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions;

/// <summary>
/// Provisions the <c>auth.{StrategyName}</c> custom authorization views a scenario configures. The DDL and
/// identifier quoting differ per engine, so each step builds its statements from
/// <see cref="AppSettings.DatabaseEngine"/> and connects with the host-side admin connection string the E2E
/// orchestration already resolved (<see cref="AppSettings.DataStoreAdminConnectionString"/>) rather than
/// re-deriving a host, port, or credentials here.
/// </summary>
[Binding]
public sealed partial class CustomViewStepDefinitions
{
    private readonly ScenarioContext _scenarioContext;

    public CustomViewStepDefinitions(ScenarioContext scenarioContext)
    {
        _scenarioContext = scenarioContext;
    }

    [Given("the custom auth view {string} authorizes Student {string}")]
    public static async Task GivenTheCustomAuthViewAuthorizesStudent(
        string strategyName,
        string studentUniqueId
    )
    {
        var escapedStudentUniqueId = studentUniqueId.Replace("'", "''", StringComparison.Ordinal);

        await CreateCustomAuthViewAsync(
            strategyName,
            selectList: Quote("DocumentId"),
            whereClause: $"{Quote("StudentUniqueId")} = '{escapedStudentUniqueId}'"
        );
    }

    [Given("the custom auth view {string} authorizes no Students")]
    public static async Task GivenTheCustomAuthViewAuthorizesNoStudents(string strategyName)
    {
        await CreateCustomAuthViewAsync(strategyName, selectList: Quote("DocumentId"), whereClause: "1 = 0");
    }

    [Given("the custom auth view {string} omits DocumentId")]
    public static async Task GivenTheCustomAuthViewOmitsDocumentId(string strategyName)
    {
        await CreateCustomAuthViewAsync(strategyName, selectList: Quote("StudentUniqueId"));
    }

    [Then("the response body should contain {string}")]
    public async Task ThenTheResponseBodyShouldContain(string expectedText)
    {
        var response = _scenarioContext.Get<IAPIResponse>("apiResponse");
        string body = await response.TextAsync();
        body.Should().Contain(expectedText);
    }

    /// <summary>
    /// Drops any existing <c>auth.{strategyName}</c> object and creates the view over
    /// <c>edfi.Student</c>. SQL Server has no <c>CREATE OR REPLACE VIEW</c>, so both engines take the
    /// drop-then-create path.
    /// </summary>
    private static async Task CreateCustomAuthViewAsync(
        string strategyName,
        string selectList,
        string? whereClause = null
    )
    {
        ValidateStrategyName(strategyName);

        await using DbConnection connection = CreateConnection();
        await connection.OpenAsync();

        foreach (var sql in BuildAuthObjectResetStatements(strategyName))
        {
            await ExecuteNonQueryAsync(connection, sql);
        }

        var where = whereClause is null ? string.Empty : $"{Environment.NewLine}WHERE {whereClause}";
        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE VIEW {Quote("auth")}.{Quote(strategyName)} AS
            SELECT {selectList}
            FROM {Quote("edfi")}.{Quote("Student")}{where};
            """
        );
    }

    /// <summary>
    /// Statements that make <c>auth.{strategyName}</c> absent and the <c>auth</c> schema present. SQL
    /// Server rejects <c>CREATE SCHEMA</c> outside its own batch and has no <c>IF NOT EXISTS</c> form, so
    /// it is guarded with a catalog check instead of PostgreSQL's <c>CREATE SCHEMA IF NOT EXISTS</c>.
    /// </summary>
    private static IReadOnlyList<string> BuildAuthObjectResetStatements(string strategyName)
    {
        if (IsMssql)
        {
            var escapedStrategyName = strategyName.Replace("'", "''", StringComparison.Ordinal);

            return
            [
                "IF SCHEMA_ID('auth') IS NULL EXEC('CREATE SCHEMA [auth];');",
                $"DROP VIEW IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
                $"DROP TABLE IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
                // A synonym would also resolve as auth.{StrategyName} and shadow the created view.
                $"IF EXISTS (SELECT 1 FROM sys.synonyms WHERE name = '{escapedStrategyName}' AND schema_id = SCHEMA_ID('auth')) DROP SYNONYM {Quote("auth")}.{Quote(strategyName)};",
            ];
        }

        return
        [
            "CREATE SCHEMA IF NOT EXISTS auth;",
            $"DROP VIEW IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
            $"DROP TABLE IF EXISTS {Quote("auth")}.{Quote(strategyName)};",
        ];
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static DbConnection CreateConnection()
    {
        var connectionString = AppSettings.DataStoreAdminConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Custom auth view provisioning requires the host-side data-store admin connection string; "
                    + "run the E2E suite through the standard orchestration so AppSettings:DataStoreAdminConnectionString is set."
            );
        }

        return IsMssql ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);
    }

    private static bool IsMssql =>
        string.Equals(AppSettings.DatabaseEngine, "mssql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Quotes an identifier for the selected engine: brackets on SQL Server, double quotes on PostgreSQL.
    /// Identifiers reaching here are either literals in this file or already validated by
    /// <see cref="ValidateStrategyName"/>, so no embedded delimiter can appear; the doubling is defensive.
    /// </summary>
    private static string Quote(string identifier) =>
        IsMssql
            ? $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]"
            : $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void ValidateStrategyName(string strategyName)
    {
        if (!Regex.IsMatch(strategyName, "^[A-Za-z][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException(
                $"Invalid custom auth view strategy name '{strategyName}'.",
                nameof(strategyName)
            );
        }
    }
}
