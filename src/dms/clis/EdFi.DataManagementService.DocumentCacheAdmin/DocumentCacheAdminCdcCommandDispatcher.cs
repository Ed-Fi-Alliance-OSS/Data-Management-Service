// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

/// <summary>
/// One cdc verb invocation, reduced to the facts the operator supplied on the command line. Everything
/// else about the operation comes from configuration or from the instance database.
/// </summary>
internal sealed record DocumentCacheAdminCdcCommandRequest(
    string VerbName,
    DocumentCacheTargetKey TargetKey,
    string? DatabaseCreationMode,
    string? WriteAdmission,
    long? PreviousGeneration,
    string? BindingJson
);

/// <summary>
/// The governed names one cdc verb operates on, for the human-readable rendering. Every field is a
/// derived governed name or an opaque operator-supplied key: no connection string, credential, or
/// tenant display name reaches this record.
/// </summary>
internal sealed record DocumentCacheAdminCdcGovernedNames(
    string ConnectorName,
    string Provider,
    string DataStoreId,
    string InstanceKey,
    string TopicName,
    string ProgressTopicName,
    string? SchemaHistoryTopicName
);

/// <summary>
/// The outcome of one cdc verb. Exactly one contract is carried, or none plus the diagnostics saying
/// why the verb produced no contract at all.
/// </summary>
internal sealed record DocumentCacheAdminCdcCommandResult(
    string VerbName,
    int ExitCode,
    string Outcome,
    string Category,
    object? Contract,
    Type? ContractType,
    IReadOnlyList<CdcDiagnostic> Diagnostics,
    DocumentCacheAdminCdcGovernedNames? GovernedNames = null
)
{
    public static DocumentCacheAdminCdcCommandResult ForContract<TContract>(
        string verbName,
        TContract contract,
        int exitCode,
        string outcome,
        string category,
        DocumentCacheAdminCdcGovernedNames? governedNames
    )
        where TContract : notnull =>
        new(verbName, exitCode, outcome, category, contract, typeof(TContract), [], governedNames);

    public static DocumentCacheAdminCdcCommandResult Refused(
        string verbName,
        int exitCode,
        string outcome,
        string category,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) => new(verbName, exitCode, outcome, category, null, null, diagnostics);
}

/// <summary>
/// Reduces one parsed cdc invocation to the request the dispatcher executes.
/// </summary>
/// <remarks>
/// Only the operator's own command-line facts are read here. The exact-token evidence flags are carried
/// through verbatim rather than interpreted: the surface has already refused anything but the exact
/// token, and the proof factory is the single place that decides whether the assertion was made.
/// </remarks>
internal static class DocumentCacheAdminCdcCommandRequestBuilder
{
    public static bool TryBuild(
        ParseResult parseResult,
        string verbName,
        DocumentCacheAdminInvocationTarget invocationTarget,
        Func<string, string> bindingJsonLoader,
        out DocumentCacheAdminCdcCommandRequest? request,
        out string? failure
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(invocationTarget);
        ArgumentNullException.ThrowIfNull(bindingJsonLoader);

        request = null;
        failure = null;

        string? bindingJson = null;
        if (
            parseResult.GetResult(DocumentCacheAdminCommandSurface.BindingJsonOptionName)
                is OptionResult { Implicit: false } bindingJsonResult
            && bindingJsonResult.GetValueOrDefault<string?>() is { Length: > 0 } bindingJsonPath
        )
        {
            try
            {
                bindingJson = bindingJsonLoader(bindingJsonPath);
            }
            catch (Exception exception) when (IsExpectedBindingJsonInputFailure(exception))
            {
                failure =
                    $"Unable to read {DocumentCacheAdminCommandSurface.BindingJsonOptionName} input: {exception.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(bindingJson))
            {
                failure =
                    $"{DocumentCacheAdminCommandSurface.BindingJsonOptionName} input was empty; adoption requires the complete binding record.";
                return false;
            }
        }

        request = new DocumentCacheAdminCdcCommandRequest(
            verbName,
            invocationTarget.TargetKey,
            OptionValue(parseResult, DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName),
            OptionValue(parseResult, DocumentCacheAdminCommandSurface.WriteAdmissionOptionName),
            parseResult.GetValue<long?>(DocumentCacheAdminCommandSurface.PreviousGenerationOptionName),
            bindingJson
        );
        return true;
    }

    private static string? OptionValue(ParseResult parseResult, string optionName) =>
        parseResult.GetResult(optionName) is OptionResult { Implicit: false } optionResult
            ? optionResult.GetValueOrDefault<string?>()
            : null;

    private static bool IsExpectedBindingJsonInputFailure(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ObjectDisposedException
        || exception is ArgumentException and not ArgumentNullException and not ArgumentOutOfRangeException;
}

internal interface IDocumentCacheAdminCdcCommandDispatcher
{
    Task<DocumentCacheAdminCdcCommandResult> ExecuteAsync(
        DocumentCacheAdminCdcCommandRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Maps each cdc verb onto <see cref="ICdcSetupController"/>.
/// </summary>
/// <remarks>
/// The dispatcher composes requests and classifies results; it makes no CDC decision of its own. The
/// provider-setup inputs come from <see cref="ICdcProviderSetupInputsFactory"/> rather than from the
/// command line, and the provider comes from the registered source-position adapter rather than from
/// re-reading the datastore setting, so the verb runs against the same provider the control plane
/// registered.
/// </remarks>
internal sealed class DocumentCacheAdminCdcCommandDispatcher(
    ICdcSetupController controller,
    ICdcProviderSetupInputsFactory providerSetupInputsFactory,
    ICdcProviderSourcePositionAdapter sourcePositions,
    IConnectionStringProvider connectionStringProvider,
    IOptions<CdcControlOptions> options,
    TimeProvider timeProvider
) : IDocumentCacheAdminCdcCommandDispatcher
{
    public async Task<DocumentCacheAdminCdcCommandResult> ExecuteAsync(
        DocumentCacheAdminCdcCommandRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset now = timeProvider.GetUtcNow();
        CdcProvider provider = sourcePositions.Provider;

        if (
            connectionStringProvider.GetConnectionString(
                request.TargetKey.DataStoreId,
                NullableTenant(request.TargetKey.TenantKey)
            )
            is not { Length: > 0 } connectionString
        )
        {
            // The identifiers are the operator's own invocation arguments and the surrogate is already
            // the safe form of them, so naming the target here publishes nothing new.
            return Refused(
                request.VerbName,
                "cdcConnectionStringUnresolved",
                CdcDiagnosticCategory.SourceMismatch,
                CdcDiagnosticComponent.ProviderSetup,
                "CDC operation could not resolve the instance database for the invocation target.",
                "absent",
                now
            );
        }

        CdcContractReadResult<CdcProviderSetupInputs> setupInputs = await providerSetupInputsFactory
            .CreateAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        if (setupInputs.Contract is not { } providerSetup)
        {
            return DocumentCacheAdminCdcCommandResult.Refused(
                request.VerbName,
                DocumentCacheAdminExitCodes.ConfigurationError,
                "configurationError",
                "cdcProviderSetupInputs",
                setupInputs.Diagnostics
            );
        }

        Invocation invocation = new(
            request,
            NewToken(),
            connectionString,
            providerSetup,
            GovernedNames(request, provider)
        );

        return request.VerbName switch
        {
            DocumentCacheAdminCommandSurface.CdcEnableVerbName => await EnableAsync(
                    invocation,
                    cancellationToken
                )
                .ConfigureAwait(false),
            DocumentCacheAdminCommandSurface.CdcStatusVerbName => Status(
                invocation,
                await controller
                    .StatusAsync(TargetRequest(invocation), cancellationToken)
                    .ConfigureAwait(false),
                "cdcStatus"
            ),
            DocumentCacheAdminCommandSurface.CdcRestartVerbName => Status(
                invocation,
                await controller
                    .RestartAsync(TargetRequest(invocation), cancellationToken)
                    .ConfigureAwait(false),
                "cdcRestart"
            ),
            DocumentCacheAdminCommandSurface.CdcAdoptVerbName => await AdoptAsync(
                    invocation,
                    now,
                    cancellationToken
                )
                .ConfigureAwait(false),
            DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName => await ReplaceSourceAsync(
                    invocation,
                    cancellationToken
                )
                .ConfigureAwait(false),
            DocumentCacheAdminCommandSurface.CdcRetireVerbName => Retire(
                invocation,
                await controller
                    .RetireAsync(TargetRequest(invocation), cancellationToken)
                    .ConfigureAwait(false)
            ),
            _ => throw new InvalidOperationException($"'{request.VerbName}' is not a cdc verb."),
        };
    }

    private async Task<DocumentCacheAdminCdcCommandResult> EnableAsync(
        Invocation invocation,
        CancellationToken cancellationToken
    )
    {
        CdcAdmission admission = await controller
            .EnableAsync(
                new CdcEnableRequest(
                    invocation.OperationId,
                    invocation.Request.TargetKey.TenantKey,
                    invocation.Request.TargetKey.DataStoreId,
                    invocation.ConnectionString,
                    Evidence(invocation),
                    invocation.ProviderSetup
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return Admission(invocation, admission, "cdcEnable");
    }

    private async Task<DocumentCacheAdminCdcCommandResult> ReplaceSourceAsync(
        Invocation invocation,
        CancellationToken cancellationToken
    )
    {
        // The surface requires the option, so an absent value here would be a surface defect rather than
        // operator input; it is still checked, because a replacement that guessed which generation it
        // supersedes is exactly the inference the design forbids.
        if (invocation.Request.PreviousGeneration is not { } previousGeneration)
        {
            return Refused(
                invocation.Request.VerbName,
                "cdcPreviousGenerationMissing",
                CdcDiagnosticCategory.BindingMismatch,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement requires the generation being replaced to be named explicitly.",
                "absent",
                timeProvider.GetUtcNow()
            );
        }

        CdcAdmission admission = await controller
            .ReplaceSourceAsync(
                new CdcReplaceSourceRequest(
                    invocation.OperationId,
                    invocation.Request.TargetKey.TenantKey,
                    invocation.Request.TargetKey.DataStoreId,
                    invocation.ConnectionString,
                    previousGeneration,
                    Evidence(invocation),
                    invocation.ProviderSetup
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return Admission(invocation, admission, "cdcReplaceSource");
    }

    private async Task<DocumentCacheAdminCdcCommandResult> AdoptAsync(
        Invocation invocation,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (invocation.Request.BindingJson is not { Length: > 0 } bindingJson)
        {
            return Refused(
                invocation.Request.VerbName,
                "cdcAdoptionBindingMissing",
                CdcDiagnosticCategory.BindingMissing,
                CdcDiagnosticComponent.Binding,
                "CDC adoption requires the complete binding record the operator is adopting under.",
                "absent",
                now
            );
        }

        CdcContractReadResult<CdcBinding> binding = CdcJsonContract.Deserialize<CdcBinding>(bindingJson);
        if (binding.Contract is not { } adoptedBinding)
        {
            return DocumentCacheAdminCdcCommandResult.Refused(
                invocation.Request.VerbName,
                DocumentCacheAdminExitCodes.ArgumentError,
                "argumentError",
                "cdcAdoptionBinding",
                binding.Diagnostics
            );
        }

        CdcContractReadResult<CdcAdoptionProof> proof = await controller
            .AdoptAsync(
                new CdcAdoptRequest(
                    invocation.OperationId,
                    adoptedBinding,
                    invocation.ConnectionString,
                    invocation.ProviderSetup
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return proof.Contract is { } adoptionProof
            ? DocumentCacheAdminCdcCommandResult.ForContract(
                invocation.Request.VerbName,
                adoptionProof,
                DocumentCacheAdminExitCodes.Success,
                "completed",
                "cdcAdopt",
                invocation.GovernedNames
            )
            : DocumentCacheAdminCdcCommandResult.Refused(
                invocation.Request.VerbName,
                DocumentCacheAdminExitCodes.RejectedNoMutation,
                "rejectedNoMutation",
                "cdcAdopt",
                proof.Diagnostics
            );
    }

    private static DocumentCacheAdminCdcCommandResult Retire(
        Invocation invocation,
        CdcContractReadResult<CdcCleanupProof> proof
    ) =>
        proof.Contract is { } cleanupProof
            ? DocumentCacheAdminCdcCommandResult.ForContract(
                invocation.Request.VerbName,
                cleanupProof,
                DocumentCacheAdminExitCodes.Success,
                "completed",
                "cdcRetire",
                invocation.GovernedNames
            )
            // A partial teardown leaves the binding record intact so the retry stays idempotent, which is
            // exactly the incomplete-and-retryable classification.
            : DocumentCacheAdminCdcCommandResult.Refused(
                invocation.Request.VerbName,
                DocumentCacheAdminExitCodes.IncompleteRetryable,
                "incompleteRetryable",
                "cdcRetire",
                proof.Diagnostics
            );

    private static DocumentCacheAdminCdcCommandResult Admission(
        Invocation invocation,
        CdcAdmission admission,
        string category
    ) =>
        DocumentCacheAdminCdcCommandResult.ForContract(
            invocation.Request.VerbName,
            admission,
            DocumentCacheAdminExitCodeMapper.ForAdmission(admission),
            LowerCamel(admission.AdmissionState.ToString()),
            category,
            invocation.GovernedNames
        );

    private static DocumentCacheAdminCdcCommandResult Status(
        Invocation invocation,
        CdcStatus status,
        string category
    ) =>
        DocumentCacheAdminCdcCommandResult.ForContract(
            invocation.Request.VerbName,
            status,
            DocumentCacheAdminExitCodeMapper.ForStatus(status),
            LowerCamel(status.Readiness.ToString()),
            category,
            invocation.GovernedNames
        );

    /// <summary>
    /// The governed names for the human rendering, or null when the configured artifact identity does
    /// not render a valid name set. A rendering failure is never reported as a CDC verdict here: the
    /// controller reaches the same generator and reports it as the operation's own diagnostics.
    /// </summary>
    private DocumentCacheAdminCdcGovernedNames? GovernedNames(
        DocumentCacheAdminCdcCommandRequest request,
        CdcProvider provider
    )
    {
        CdcControlOptions controlOptions = options.Value;
        CdcArtifactNameResult names = CdcArtifactNameGenerator.Render(
            new CdcArtifactNameInput(
                controlOptions.DeploymentKey,
                controlOptions.TopicPrefix,
                controlOptions.InstanceKey,
                controlOptions.Generation,
                provider
            )
        );

        return names.Inventory is not { } inventory
            ? null
            : new DocumentCacheAdminCdcGovernedNames(
                inventory.ConnectorName,
                LowerCamel(provider.ToString()),
                request.TargetKey.DataStoreId.ToString(CultureInfo.InvariantCulture),
                inventory.InstanceKey,
                inventory.TopicName,
                inventory.ProgressTopicName,
                inventory.SchemaHistoryTopicName
            );
    }

    private static DocumentCacheAdminCdcCommandResult Refused(
        string verbName,
        string code,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string observed,
        DateTimeOffset now
    ) =>
        DocumentCacheAdminCdcCommandResult.Refused(
            verbName,
            DocumentCacheAdminExitCodes.RejectedNoMutation,
            "rejectedNoMutation",
            code,
            [
                new CdcDiagnostic(
                    code,
                    category,
                    CdcDiagnosticSeverity.Error,
                    component,
                    now,
                    message,
                    retryable: false,
                    observed: observed
                ),
            ]
        );

    private static CdcTargetOperationRequest TargetRequest(Invocation invocation) =>
        new(
            invocation.OperationId,
            invocation.Request.TargetKey.TenantKey,
            invocation.Request.TargetKey.DataStoreId,
            invocation.ConnectionString,
            invocation.ProviderSetup
        );

    /// <summary>
    /// The provisioning evidence is exactly the operator's own tokens. A near-miss token is passed
    /// through unchanged rather than corrected, so the proof factory is the single place that decides
    /// whether the assertion was made.
    /// </summary>
    private static CdcProvisioningProofEvidence Evidence(Invocation invocation) =>
        new(
            invocation.OperationId,
            invocation.Request.DatabaseCreationMode,
            invocation.Request.WriteAdmission
        );

    /// <summary>
    /// The normalized default tenant is the empty string in a target key, but the connection-string
    /// provider expects a null tenant for the single-tenant case.
    /// </summary>
    private static string? NullableTenant(string tenantKey) =>
        string.IsNullOrEmpty(tenantKey) ? null : tenantKey;

    /// <summary>
    /// Operation and run identifiers must be safe tokens, so a lowercase-hex GUID is used rather than
    /// the default GUID format.
    /// </summary>
    private static string NewToken() => Guid.NewGuid().ToString("N").ToLowerInvariant();

    private static string LowerCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>
    /// One cdc verb's resolved inputs, so each verb reads them rather than having six of them threaded
    /// through its signature.
    /// </summary>
    private sealed record Invocation(
        DocumentCacheAdminCdcCommandRequest Request,
        string OperationId,
        string ConnectionString,
        CdcProviderSetupInputs ProviderSetup,
        DocumentCacheAdminCdcGovernedNames? GovernedNames
    );
}
