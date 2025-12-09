// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Infrastructure;
using Microsoft.Extensions.Logging;
using Reqnroll;
using Reqnroll.BoDi;
using Serilog;
using Serilog.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Hooks;

/// <summary>
/// Registers infrastructure services for dependency injection in Reqnroll scenarios.
/// </summary>
[Binding]
public class DependencyInjectionHooks(IObjectContainer container)
{
    [BeforeScenario(Order = 0)]
    public void RegisterInfrastructureServices()
    {
        var loggerFactory = new SerilogLoggerFactory(Log.Logger);

        // Register loggers
        var kafkaAdminLogger = loggerFactory.CreateLogger<KafkaAdminClient>();
        var debeziumLogger = loggerFactory.CreateLogger<DebeziumConnectorClient>();
        var postgresCleanupLogger = loggerFactory.CreateLogger<PostgresReplicationCleanup>();
        var infrastructureManagerLogger = loggerFactory.CreateLogger<InstanceInfrastructureManager>();

        container.RegisterInstanceAs(kafkaAdminLogger);
        container.RegisterInstanceAs(debeziumLogger);
        container.RegisterInstanceAs(postgresCleanupLogger);
        container.RegisterInstanceAs(infrastructureManagerLogger);

        // Register infrastructure clients
        var kafkaAdmin = new KafkaAdminClient(kafkaAdminLogger);
        var debeziumClient = new DebeziumConnectorClient(debeziumLogger);
        var postgresCleanup = new PostgresReplicationCleanup(postgresCleanupLogger);

        container.RegisterInstanceAs(kafkaAdmin);
        container.RegisterInstanceAs(debeziumClient);
        container.RegisterInstanceAs(postgresCleanup);

        // Register the orchestrator
        var infrastructureManager = new InstanceInfrastructureManager(
            kafkaAdmin,
            debeziumClient,
            postgresCleanup,
            infrastructureManagerLogger
        );
        container.RegisterInstanceAs(infrastructureManager);
    }

    [AfterScenario(Order = 10000)]
    public void DisposeInfrastructureServices()
    {
        // Dispose the infrastructure manager (which disposes the Debezium client)
        if (container.IsRegistered<InstanceInfrastructureManager>())
        {
            var manager = container.Resolve<InstanceInfrastructureManager>();
            manager.Dispose();
        }
    }
}
