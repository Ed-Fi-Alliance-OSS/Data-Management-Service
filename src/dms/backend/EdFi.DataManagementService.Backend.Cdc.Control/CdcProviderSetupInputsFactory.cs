// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using Microsoft.Extensions.Options;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Builds the provider-setup inputs one cdc operation is issued with.
/// </summary>
/// <remarks>
/// The two inventories are not operator input: they are what the instance database's own schema
/// emission describes, so deriving them anywhere but from that emission would let a caller assert a
/// source shape the database does not have. The two principals are deployment facts and come from
/// configuration. This lives beside the controller rather than in a host, because every host that
/// drives the controller — the operator CLI, bootstrap, the E2E harness — needs the same derivation.
/// </remarks>
public interface ICdcProviderSetupInputsFactory
{
    /// <summary>
    /// Derives the inputs for one provider, or reports why they could not be derived. A partial result
    /// is never returned: provider setup reads every field, so an incomplete derivation must refuse.
    /// </summary>
    Task<CoreCdc.CdcContractReadResult<CdcProviderSetupInputs>> CreateAsync(
        CoreCdc.CdcProvider provider,
        CancellationToken cancellationToken = default
    );
}

internal sealed class CdcProviderSetupInputsFactory(
    IOptions<CdcControlOptions> options,
    IMappingSetProvider mappingSetProvider,
    IEnumerable<IRuntimeMappingSetCompiler> runtimeMappingSetCompilers,
    TimeProvider timeProvider
) : ICdcProviderSetupInputsFactory
{
    public async Task<CoreCdc.CdcContractReadResult<CdcProviderSetupInputs>> CreateAsync(
        CoreCdc.CdcProvider provider,
        CancellationToken cancellationToken = default
    )
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        CdcControlOptions controlOptions = options.Value;
        CoreCdc.CdcDiagnosticCollector diagnostics = new(now);

        RequirePrincipal(
            controlOptions.SetupPrincipal,
            nameof(CdcControlOptions.SetupPrincipal),
            "$.setupPrincipal",
            now,
            diagnostics
        );
        RequirePrincipal(
            controlOptions.ConnectorPrincipal,
            nameof(CdcControlOptions.ConnectorPrincipal),
            "$.connectorPrincipal",
            now,
            diagnostics
        );

        if (diagnostics.HasDiagnostics)
        {
            return CoreCdc.CdcContractReadResult<CdcProviderSetupInputs>.Failure(diagnostics.Diagnostics);
        }

        SqlDialect dialect = ToSqlDialect(provider);
        IRuntimeMappingSetCompiler? compiler = runtimeMappingSetCompilers.SingleOrDefault(candidate =>
            candidate.Dialect == dialect
        );
        if (compiler is null)
        {
            return Refused(
                "providerSetupInputsCompilerMissing",
                "$.expectedSourceInventory",
                "CDC provider setup inputs require the runtime mapping set compiler for the "
                    + "deployment's datastore.",
                $"one compiler for {dialect}",
                "absent",
                now
            );
        }

        MappingSet mappingSet;
        try
        {
            mappingSet = await mappingSetProvider
                .GetOrCreateAsync(compiler.GetCurrentKey(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MappingSetUnavailableException)
        {
            // The message carries schema detail this boundary does not publish, so only the fact that
            // the authoritative schema could not be resolved crosses it.
            return Refused(
                "providerSetupInputsSchemaUnavailable",
                "$.expectedSourceInventory",
                "CDC provider setup inputs require the authoritative effective schema, which could "
                    + "not be resolved.",
                "a resolved mapping set",
                "unavailable",
                now
            );
        }
        catch (InvalidOperationException)
        {
            return Refused(
                "providerSetupInputsSchemaUnavailable",
                "$.expectedSourceInventory",
                "CDC provider setup inputs require the authoritative effective schema, which could "
                    + "not be resolved.",
                "a resolved mapping set",
                "unavailable",
                now
            );
        }

        FullDdlEmission emission = FullDdlEmitter.EmitWithMetadata(
            SqlDialectFactory.Create(dialect),
            mappingSet.Model
        );

        return CoreCdc.CdcContractReadResult<CdcProviderSetupInputs>.Success(
            new CdcProviderSetupInputs(
                controlOptions.SetupPrincipal,
                controlOptions.ConnectorPrincipal,
                emission.CdcSourceInventory,
                emission.CdcDmsManagedTableInventory
            )
        );
    }

    private static void RequirePrincipal(
        string value,
        string optionName,
        string path,
        DateTimeOffset now,
        CoreCdc.CdcDiagnosticCollector diagnostics
    )
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        diagnostics.Add(
            Diagnostic(
                "providerSetupInputsPrincipalMissing",
                path,
                $"CDC provider setup inputs require {CdcControlOptions.SectionName}:{optionName}.",
                "a configured principal",
                "absent",
                now
            )
        );
    }

    private static CoreCdc.CdcContractReadResult<CdcProviderSetupInputs> Refused(
        string code,
        string path,
        string message,
        string expected,
        string observed,
        DateTimeOffset now
    ) =>
        CoreCdc.CdcContractReadResult<CdcProviderSetupInputs>.Failure([
            Diagnostic(code, path, message, expected, observed, now),
        ]);

    private static CoreCdc.CdcDiagnostic Diagnostic(
        string code,
        string path,
        string message,
        string expected,
        string observed,
        DateTimeOffset now
    ) =>
        new CoreCdc.CdcDiagnostic(
            code,
            CoreCdc.CdcDiagnosticCategory.ProviderSetupInvalid,
            CoreCdc.CdcDiagnosticSeverity.Error,
            CoreCdc.CdcDiagnosticComponent.ProviderSetup,
            now,
            message,
            retryable: false,
            artifactKind: "providerSetupInputs",
            expected: expected,
            observed: observed
        ).WithPath(path);

    private static SqlDialect ToSqlDialect(CoreCdc.CdcProvider provider) =>
        provider == CoreCdc.CdcProvider.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql;
}
