// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The provider-setup inputs are derived from the authoritative effective schema, never from caller
/// input, so these prove the derivation refuses rather than guesses whenever the schema or a deployment
/// principal is not available.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcProviderSetupInputs")]
public class Given_CdcProviderSetupInputsFactoryTests
{
    private const string SetupPrincipal = "setup_principal";
    private const string ConnectorPrincipal = "connector_principal";

    [TestCase(CoreCdc.CdcProvider.Postgresql)]
    [TestCase(CoreCdc.CdcProvider.SqlServer)]
    public async Task It_refuses_when_the_runtime_mapping_set_compiler_for_the_provider_is_absent(
        CoreCdc.CdcProvider provider
    )
    {
        CdcContractReadResult<CdcProviderSetupInputs> result = await Factory(
                A.Fake<IMappingSetProvider>(),
                compilers: []
            )
            .CreateAsync(provider);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "providerSetupInputsCompilerMissing");
    }

    [Test]
    public async Task It_refuses_when_the_setup_principal_is_not_configured()
    {
        CdcContractReadResult<CdcProviderSetupInputs> result = await Factory(
                A.Fake<IMappingSetProvider>(),
                compilers: [],
                setupPrincipal: "   "
            )
            .CreateAsync(CoreCdc.CdcProvider.Postgresql);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "providerSetupInputsPrincipalMissing"
                && diagnostic.Path == "$.setupPrincipal"
            );
    }

    [Test]
    public async Task It_refuses_when_the_connector_principal_is_not_configured()
    {
        CdcContractReadResult<CdcProviderSetupInputs> result = await Factory(
                A.Fake<IMappingSetProvider>(),
                compilers: [],
                connectorPrincipal: ""
            )
            .CreateAsync(CoreCdc.CdcProvider.Postgresql);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "providerSetupInputsPrincipalMissing"
                && diagnostic.Path == "$.connectorPrincipal"
            );
    }

    /// <summary>
    /// A missing principal is reported before the schema is resolved, so an operator sees the setting
    /// they have to supply rather than a schema failure caused by it.
    /// </summary>
    [Test]
    public async Task It_reports_both_missing_principals_before_resolving_the_schema()
    {
        IMappingSetProvider mappingSetProvider = A.Fake<IMappingSetProvider>();

        CdcContractReadResult<CdcProviderSetupInputs> result = await Factory(
                mappingSetProvider,
                compilers: [],
                setupPrincipal: "",
                connectorPrincipal: ""
            )
            .CreateAsync(CoreCdc.CdcProvider.Postgresql);

        result.Diagnostics.Should().HaveCount(2);
        A.CallTo(mappingSetProvider).MustNotHaveHappened();
    }

    [Test]
    public async Task It_refuses_when_the_authoritative_schema_cannot_be_resolved()
    {
        IMappingSetProvider mappingSetProvider = A.Fake<IMappingSetProvider>();
        A.CallTo(() => mappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
            .Throws(new MappingSetUnavailableException("mapping pack sentinel detail"));

        CdcContractReadResult<CdcProviderSetupInputs> result = await Factory(
                mappingSetProvider,
                compilers: [Compiler(SqlDialect.Pgsql)]
            )
            .CreateAsync(CoreCdc.CdcProvider.Postgresql);

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "providerSetupInputsSchemaUnavailable");
    }

    /// <summary>
    /// The unavailability message carries schema detail this boundary does not publish, so only the fact
    /// crosses it.
    /// </summary>
    [Test]
    public async Task It_does_not_publish_the_schema_failure_detail()
    {
        IMappingSetProvider mappingSetProvider = A.Fake<IMappingSetProvider>();
        A.CallTo(() => mappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
            .Throws(new MappingSetUnavailableException("mapping pack sentinel detail"));

        CdcContractReadResult<CdcProviderSetupInputs> result = await Factory(
                mappingSetProvider,
                compilers: [Compiler(SqlDialect.Pgsql)]
            )
            .CreateAsync(CoreCdc.CdcProvider.Postgresql);

        string rendered = string.Join(
            '\n',
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Message}|{diagnostic.Expected}|{diagnostic.Observed}"
            )
        );

        rendered.Should().NotContain("sentinel");
    }

    [Test]
    public async Task It_selects_the_compiler_for_the_requested_provider_dialect()
    {
        IRuntimeMappingSetCompiler postgresql = Compiler(SqlDialect.Pgsql);
        IRuntimeMappingSetCompiler sqlServer = Compiler(SqlDialect.Mssql);
        IMappingSetProvider mappingSetProvider = A.Fake<IMappingSetProvider>();
        A.CallTo(() => mappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
            .Throws(new MappingSetUnavailableException("unavailable"));

        _ = await Factory(mappingSetProvider, compilers: [postgresql, sqlServer])
            .CreateAsync(CoreCdc.CdcProvider.SqlServer);

        A.CallTo(() => sqlServer.GetCurrentKey()).MustHaveHappened();
        A.CallTo(() => postgresql.GetCurrentKey()).MustNotHaveHappened();
    }

    private static ICdcProviderSetupInputsFactory Factory(
        IMappingSetProvider mappingSetProvider,
        IEnumerable<IRuntimeMappingSetCompiler> compilers,
        string setupPrincipal = SetupPrincipal,
        string connectorPrincipal = ConnectorPrincipal
    ) =>
        new CdcProviderSetupInputsFactory(
            Options.Create(
                new CdcControlOptions
                {
                    SetupPrincipal = setupPrincipal,
                    ConnectorPrincipal = connectorPrincipal,
                }
            ),
            mappingSetProvider,
            compilers,
            TimeProvider.System
        );

    private static IRuntimeMappingSetCompiler Compiler(SqlDialect dialect)
    {
        IRuntimeMappingSetCompiler compiler = A.Fake<IRuntimeMappingSetCompiler>();
        A.CallTo(() => compiler.Dialect).Returns(dialect);
        A.CallTo(() => compiler.GetCurrentKey()).Returns(new MappingSetKey("hash", dialect, "1.0"));
        return compiler;
    }
}
