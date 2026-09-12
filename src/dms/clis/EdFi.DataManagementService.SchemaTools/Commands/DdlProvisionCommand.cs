// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.SchemaTools.Commands;

/// <summary>
/// Defines the <c>ddl provision</c> subcommand: generates DDL and executes it against
/// a target database. Provisions one database at a time (no 'both' dialect).
/// </summary>
public static class DdlProvisionCommand
{
    public static Command Create(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder
    )
    {
        var schemaOption = new Option<string[]>("--schema", "-s")
        {
            Description = "ApiSchema.json path(s). First is core, rest are extensions.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };

        var connectionStringOption = new Option<string>("--connection-string", "-c")
        {
            Description = "ADO.NET connection string for the target database.",
            Required = true,
        };

        var dialectOption = new Option<string>("--dialect", "-d")
        {
            Description = "SQL dialect: pgsql or mssql",
            Required = true,
        };
        dialectOption.AcceptOnlyFromAmong("pgsql", "mssql");

        var createDatabaseOption = new Option<bool>("--create-database")
        {
            Description = "Create the target database if it does not exist before provisioning.",
            DefaultValueFactory = _ => false,
        };

        var timeoutOption = new Option<int>("--timeout", "-t")
        {
            Description = "Command timeout in seconds for DDL execution (default: 300).",
            DefaultValueFactory = _ => 300,
        };

        var statePathOption = new Option<string>("--managed-state-path")
        {
            Description =
                "Controller state root for exclusively managed provisioning; emits one JSON receipt result.",
            DefaultValueFactory = _ => string.Empty,
        };
        var purposeOption = new Option<string>("--managed-workflow-purpose")
        {
            Description =
                "Creation-time purpose: source-history-only (default), or initial-cdc-provisioning for a trusted offline CDC host.",
            DefaultValueFactory = _ => "source-history-only",
        };
        purposeOption.AcceptOnlyFromAmong("source-history-only", "initial-cdc-provisioning");
        var prerequisitesOption = new Option<string>("--cdc-projection-prerequisites")
        {
            Description =
                "Managed CDC preflight: inspect, or configure nested triggers on an explicitly owned local SQL Server after a new CREATE. Retries inspect only.",
            DefaultValueFactory = _ => "none",
        };
        prerequisitesOption.AcceptOnlyFromAmong("none", "inspect", "owned-local-sql-server");
        var deploymentOption = new Option<string>("--deployment-key") { DefaultValueFactory = _ => "local" };
        var tenantOption = new Option<string>("--tenant-key") { DefaultValueFactory = _ => string.Empty };
        var dataStoreOption = new Option<string>("--data-store-id")
        {
            DefaultValueFactory = _ => string.Empty,
        };
        var instanceOption = new Option<string>("--instance-key") { DefaultValueFactory = _ => string.Empty };
        var generationOption = new Option<long>("--generation") { DefaultValueFactory = _ => 1 };

        var command = new Command("provision", "Generate DDL and execute it against a target database");
        command.Options.Add(schemaOption);
        command.Options.Add(connectionStringOption);
        command.Options.Add(dialectOption);
        command.Options.Add(createDatabaseOption);
        command.Options.Add(timeoutOption);
        command.Options.Add(statePathOption);
        command.Options.Add(prerequisitesOption);
        command.Options.Add(purposeOption);
        command.Options.Add(deploymentOption);
        command.Options.Add(tenantOption);
        command.Options.Add(dataStoreOption);
        command.Options.Add(instanceOption);
        command.Options.Add(generationOption);

        command.SetAction(parseResult =>
        {
            var schemas = parseResult.GetValue(schemaOption) ?? [];
            var connectionString = parseResult.GetValue(connectionStringOption)!;
            var dialect = parseResult.GetValue(dialectOption)!;
            var createDatabase = parseResult.GetValue(createDatabaseOption);
            var timeout = parseResult.GetValue(timeoutOption);
            return Execute(
                logger,
                fileLoader,
                schemaSetBuilder,
                schemas,
                connectionString,
                dialect,
                createDatabase,
                timeout,
                parseResult.GetValue(statePathOption)!,
                parseResult.GetValue(purposeOption) == "initial-cdc-provisioning"
                    ? CdcWorkflowPurpose.InitialCdcProvisioning
                    : CdcWorkflowPurpose.SourceHistoryOnly,
                parseResult.GetValue(prerequisitesOption) switch
                {
                    "inspect" => CdcProjectionPrerequisiteMode.Inspect,
                    "owned-local-sql-server" => CdcProjectionPrerequisiteMode.OwnedLocalSqlServer,
                    _ => CdcProjectionPrerequisiteMode.None,
                },
                new CdcTargetIdentity(
                    parseResult.GetValue(deploymentOption)!,
                    CdcTargetValidator.MapE18TenantKeyToBindingTenantKey(
                        parseResult.GetValue(tenantOption)!
                    )!,
                    parseResult.GetValue(dataStoreOption)!,
                    parseResult.GetValue(instanceOption)!,
                    parseResult.GetValue(generationOption),
                    ParseDialect(dialect) == SqlDialect.Pgsql ? CdcProvider.Postgresql : CdcProvider.SqlServer
                )
            );
        });

        return command;
    }

    private static int Execute(
        ILogger logger,
        IApiSchemaFileLoader fileLoader,
        EffectiveSchemaSetBuilder schemaSetBuilder,
        string[] schemaPaths,
        string connectionString,
        string dialectName,
        bool createDatabase,
        int commandTimeoutSeconds,
        string managedStatePath,
        CdcWorkflowPurpose workflowPurpose,
        CdcProjectionPrerequisiteMode projectionPrerequisites,
        CdcTargetIdentity managedTarget
    )
    {
        if (schemaPaths.Length == 0)
        {
            Console.Error.WriteLine("At least one --schema path is required.");
            return 1;
        }

        bool managed = managedStatePath.Length > 0;
        if (
            workflowPurpose == CdcWorkflowPurpose.InitialCdcProvisioning && !managed
            || projectionPrerequisites != CdcProjectionPrerequisiteMode.None && !managed
            || projectionPrerequisites == CdcProjectionPrerequisiteMode.OwnedLocalSqlServer
                && dialectName != "mssql"
        )
        {
            Console.Error.WriteLine(
                "CDC prerequisite preparation requires managed provisioning; local SQL Server authority requires the mssql dialect."
            );
            return 1;
        }
        if (
            managed
            && (
                !createDatabase
                || commandTimeoutSeconds <= 0
                || !CdcTargetValidator
                    .ValidateBindingIdentity(CdcBindingIdentity.FromTargetIdentity(managedTarget))
                    .Succeeded
            )
        )
        {
            Console.Error.WriteLine(
                "Managed provisioning requires --create-database and a valid target identity and timeout."
            );
            return 1;
        }
        // Existing provisioners log physical names. Managed command diagnostics must never emit these.
        if (managed)
        {
            logger = NullLogger.Instance;
        }
        var dialect = ParseDialect(dialectName);

        // Load schemas
        var corePath = schemaPaths[0];
        var extensionPaths = schemaPaths.Skip(1).ToList();

        logger.LogInformation(
            "Loading schemas: core={CorePath}, extensions={ExtensionCount}",
            LoggingSanitizer.SanitizeForLogging(corePath),
            extensionPaths.Count
        );

        var loadResult = fileLoader.Load(corePath, extensionPaths);

        if (loadResult is not ApiSchemaFileLoadResult.SuccessResult success)
        {
            return LoadResultErrorHandler.Handle(logger, loadResult);
        }

        return CommandErrorHandler.Execute(
            logger,
            "DDL provisioning",
            () =>
            {
                var result = DdlCommandHelpers.BuildDdl(
                    logger,
                    schemaSetBuilder,
                    success.NormalizedNodes,
                    dialect
                );
                var effectiveSchemaInfo = result.EffectiveSchemaSet.EffectiveSchema;

                // Create the appropriate provisioner
                var provisioner = CreateProvisioner(dialect, logger);

                if (managed)
                {
                    try
                    {
                        IDocumentCachePhysicalSourceFingerprintReader reader =
                            dialect == SqlDialect.Pgsql
                                ? new PostgresqlDocumentCachePhysicalSourceFingerprintReader(
                                    NullLogger<PostgresqlDocumentCachePhysicalSourceFingerprintReader>.Instance
                                )
                                : new MssqlDocumentCachePhysicalSourceFingerprintReader(
                                    NullLogger<MssqlDocumentCachePhysicalSourceFingerprintReader>.Instance
                                );
                        var controller = new CdcManagedDatabaseProvisioning(
                            new LocalCdcWorkflowJournalStore(managedStatePath)
                        );
                        var adapter = new ManagedDatabaseProvisioner(
                            provisioner,
                            reader,
                            connectionString,
                            effectiveSchemaInfo,
                            result.CombinedSql,
                            commandTimeoutSeconds,
                            projectionPrerequisites
                        );
                        var receipt = controller
                            .ProvisionAsync(managedTarget, adapter, purpose: workflowPurpose)
                            .GetAwaiter()
                            .GetResult();
                        Console.WriteLine(
                            JsonSerializer.Serialize(
                                receipt,
                                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                                {
                                    Converters = { new JsonStringEnumConverter() },
                                }
                            )
                        );
                        return 0;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (CdcManagedProvisioningRecoveryException exception)
                    {
                        Console.Error.WriteLine(exception.Message);
                        return 1;
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine(
                            $"Managed provisioning failed. Inspect trusted workflow evidence; interrupted creation requires cleanup/reprovisioning. ({exception.GetType().Name})"
                        );
                        return 1;
                    }
                }

                var databaseName = provisioner.GetDatabaseName(connectionString);

                // Optional: create database if requested
                var databaseWasCreated = false;
                if (createDatabase)
                {
                    databaseWasCreated = provisioner.CreateDatabaseIfNotExists(connectionString);
                }

                // Check/configure MVCC (SQL Server only; no-op for PostgreSQL).
                // Runs before DDL so a post-DDL MVCC failure cannot turn a successful
                // provision into exit code 1. Safe because ALTER DATABASE runs on
                // master independently of the target DB connection.
                provisioner.CheckOrConfigureMvcc(connectionString, databaseWasCreated);

                // Fail-fast preflight: run bounded create-only provisioning guards,
                // including schema hash, seed data, singleton state, legacy artifact,
                // and provider-prerequisite validation before executing any DDL.
                //
                // Defense-in-depth: the in-SQL validation in SeedDmlEmitter also checks
                // the hash inside the DDL transaction as an ultimate safety net.
                provisioner.PreflightSeedValidation(connectionString, effectiveSchemaInfo);

                // Execute DDL in a transaction
                provisioner.ExecuteInTransaction(connectionString, result.CombinedSql, commandTimeoutSeconds);

                // Print summary
                Console.WriteLine(
                    $"Provisioning complete for database: {LoggingSanitizer.SanitizeForConsole(databaseName)}"
                );
                Console.WriteLine(
                    $"Effective schema hash: {LoggingSanitizer.SanitizeForConsole(effectiveSchemaInfo.EffectiveSchemaHash)}"
                );
                Console.WriteLine($"Resource key count: {effectiveSchemaInfo.ResourceKeyCount}");
                Console.WriteLine($"Dialect: {LoggingSanitizer.SanitizeForConsole(dialectName)}");

                return 0;
            }
        );
    }

    private static SqlDialect ParseDialect(string dialectName)
    {
        return dialectName.ToLowerInvariant() switch
        {
            "pgsql" => SqlDialect.Pgsql,
            "mssql" => SqlDialect.Mssql,
            _ => throw new ArgumentOutOfRangeException(
                nameof(dialectName),
                dialectName,
                "Invalid dialect (should be rejected by AcceptOnlyFromAmong)"
            ),
        };
    }

    private static IDatabaseProvisioner CreateProvisioner(SqlDialect dialect, ILogger logger)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDatabaseProvisioner(logger),
            SqlDialect.Mssql => new MssqlDatabaseProvisioner(logger),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect"),
        };
    }
}
