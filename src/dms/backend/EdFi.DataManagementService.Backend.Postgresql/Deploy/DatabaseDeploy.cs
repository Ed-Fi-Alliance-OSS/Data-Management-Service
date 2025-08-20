// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using DbUp;
using EdFi.DataManagementService.Backend.Deploy;

namespace EdFi.DataManagementService.Backend.Postgresql.Deploy;

public class DatabaseDeploy : IDatabaseDeploy
{
    public DatabaseDeployResult DeployDatabase(string connectionString, bool optimizeForQueryHandler)
    {
        try
        {
            EnsureDatabase.For.PostgresqlDatabase(connectionString);
        }
        catch (Exception e)
        {
            return new DatabaseDeployResult.DatabaseDeployFailure(e);
        }

        var upgrader = DeployChanges
            .To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                script =>
                    script.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                    && (
                        optimizeForQueryHandler
                        // Exclude "*QueryHandler.sql" files if optimizeForQueryHandler is false
                        || !script.EndsWith("QueryHandler.sql", StringComparison.OrdinalIgnoreCase)
                    )
            )
            .JournalToPostgresqlTable("public", "dms_SchemaVersions")
            .WithVariablesDisabled()
            .LogScriptOutput()
            .LogToAutodetectedLog()
            .Build();

        if (!upgrader.TryConnect(out string error))
        {
            return new DatabaseDeployResult.DatabaseDeployFailure(new Exception(error));
        }

        var result = upgrader.PerformUpgrade();
        return result.Successful
            ? new DatabaseDeployResult.DatabaseDeploySuccess()
            : new DatabaseDeployResult.DatabaseDeployFailure(result.Error);
    }
}
