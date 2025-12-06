// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.Configuration;

/// <summary>
/// Compile-time constants for E2E test infrastructure.
/// Database connection strings and names are known at compile time because
/// they are created by the setup script before tests run.
/// </summary>
public static class TestConstants
{
    // Database names (created by setup script)
    public const string Database1Name = "edfi_datamanagementservice_d255901_sy2024";
    public const string Database2Name = "edfi_datamanagementservice_d255901_sy2025";
    public const string Database3Name = "edfi_datamanagementservice_d255902_sy2024";

    // Connection strings for each database (used when creating instances)
    public const string Database1ConnectionString =
        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255901_sy2024;";

    public const string Database2ConnectionString =
        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255901_sy2025;";

    public const string Database3ConnectionString =
        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255902_sy2024;";

    // Kafka infrastructure
    public const string KafkaBootstrapServers = "localhost:9092";
    public const string KafkaBootstrapServersInternal = "dms-kafka1:9092";
    public const string KafkaConnectUrl = "http://localhost:8083/connectors";

    // PostgreSQL (for replication slot cleanup)
    public const string PostgresHost = "localhost";
    public const int PostgresPort = 5435;
    public const string PostgresUser = "postgres";
    public const string PostgresPassword = "abcdefgh1!";

    /// <summary>
    /// Get database name by index (1-based)
    /// </summary>
    public static string GetDatabaseName(int index) =>
        index switch
        {
            1 => Database1Name,
            2 => Database2Name,
            3 => Database3Name,
            _ => throw new ArgumentOutOfRangeException(nameof(index), "Only databases 1-3 are available"),
        };

    /// <summary>
    /// Get connection string by index (1-based)
    /// </summary>
    public static string GetConnectionString(int index) =>
        index switch
        {
            1 => Database1ConnectionString,
            2 => Database2ConnectionString,
            3 => Database3ConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(index), "Only databases 1-3 are available"),
        };
}
