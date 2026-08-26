// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using DbUp;
using DbUp.Builder;
using DbUp.Engine.Output;
using EdFi.DmsConfigurationService.Backend.Deploy;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Deploy;

/// <summary>
/// Deploys the CMS administrative database.
/// </summary>
/// <remarks>
/// <see cref="ScriptOutputLog"/> exists so integration tests can observe the script output that
/// <c>LogScriptOutput</c> captures, which is the only channel an upgrade script has for diagnostics
/// too large to fit in the thrown error. It is internal and unset in production, where logging stays
/// on the autodetected log.
/// </remarks>
public class DatabaseDeploy : IDatabaseDeploy
{
    /// <summary>
    /// When set, script output is additionally written here. Tests use it to read the complete
    /// upgrade diagnostics; production leaves it null.
    /// </summary>
    internal IUpgradeLog? ScriptOutputLog { get; init; }

    public DatabaseDeployResult DeployDatabase(string connectionString)
    {
        try
        {
            EnsureDatabase.For.PostgresqlDatabase(connectionString);
        }
        catch (Exception e)
        {
            return new DatabaseDeployResult.DatabaseDeployFailure(e);
        }

        UpgradeEngineBuilder builder = DeployChanges
            .To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .JournalToPostgresqlTable("public", "dmscs_SchemaVersions")
            .WithVariablesDisabled()
            .LogScriptOutput()
            .LogToAutodetectedLog();

        if (ScriptOutputLog is not null)
        {
            builder = builder.LogTo(ScriptOutputLog);
        }

        var upgrader = builder.Build();

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
