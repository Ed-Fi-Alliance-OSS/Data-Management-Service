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
    string? BindingJson,
    bool ConnectorAlreadyAbsent
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
            OptionValue<string>(parseResult, DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName),
            OptionValue<string>(parseResult, DocumentCacheAdminCommandSurface.WriteAdmissionOptionName),
            OptionValue<long?>(parseResult, DocumentCacheAdminCommandSurface.PreviousGenerationOptionName),
            bindingJson,
            OptionValue<bool>(parseResult, DocumentCacheAdminCommandSurface.ConnectorAlreadyAbsentOptionName)
        );
        return true;
    }

    /// <summary>
    /// The value of an option the parsed verb declares, or the type's default when it does not declare
    /// it or the parser supplied the value implicitly.
    /// </summary>
    /// <remarks>
    /// Read through the option's own result rather than by name, because several of these options are
    /// scoped to a single verb: asking a verb that does not declare one for its value by name is
    /// outside the documented contract of that overload, whatever the current implementation returns.
    /// </remarks>
    private static T? OptionValue<T>(ParseResult parseResult, string optionName) =>
        parseResult.GetResult(optionName) is OptionResult { Implicit: false } optionResult
            ? optionResult.GetValueOrDefault<T?>()
            : default;

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
///
/// It also loads the invocation tenant's data stores, because on this path nothing else does. The
/// DocumentCache status and mutating commands reach the Configuration Service through the target
/// registry refresh their own executor branch runs first; the cdc branch dispatches straight here,
/// and <see cref="IConnectionStringProvider"/> reads an in-memory cache with no lazy load behind it.
/// </remarks>
internal sealed class DocumentCacheAdminCdcCommandDispatcher(
    ICdcSetupController controller,
    ICdcProviderSetupInputsFactory providerSetupInputsFactory,
    ICdcProviderSourcePositionAdapter sourcePositions,
    IDataStoreProvider dataStoreProvider,
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

        // Before the connection string is read, because the read alone cannot produce one: an
        // unloaded tenant answers exactly as an absent data store does.
        if (await LoadDataStoresAsync(request, cancellationToken).ConfigureAwait(false) is { } loadRefusal)
        {
            return loadRefusal;
        }

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
            //
            // The code deliberately says "instance database" rather than naming a connection string.
            // CdcDiagnostic sanitizes its own code field, and "connectionString" is one of the
            // fragments it treats as a secret - a code carrying it is replaced by "redacted" in the
            // shared contract and in stderr alike, leaving an operator with no stable token to match
            // the one refusal they most need to act on.
            return Refused(
                request.VerbName,
                "cdcInstanceDatabaseUnresolved",
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

    /// <summary>
    /// Loads the invocation tenant's data stores from the Configuration Service, or reports why the
    /// verb cannot reach the instance database it operates on.
    /// </summary>
    /// <remarks>
    /// <see cref="IConnectionStringProvider"/> reads a cache the provider fills only when it is asked
    /// to load; there is no lazy load behind the read. A tenant that was never loaded answers null
    /// exactly as an absent data store does, so without this every cdc verb would refuse with a
    /// message naming the wrong cause.
    ///
    /// It is issued here rather than through the target registry the DocumentCache commands refresh.
    /// That refresh also resolves projection-target membership, which the cdc verbs deliberately do
    /// not require: retirement runs against a stack whose DMS is already gone, and the explicit
    /// projection-target proof is the enablement's own step against raw configuration.
    /// </remarks>
    private async Task<DocumentCacheAdminCdcCommandResult?> LoadDataStoresAsync(
        DocumentCacheAdminCdcCommandRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await dataStoreProvider
                .LoadDataStores(NullableTenant(request.TargetKey.TenantKey), cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (InvalidOperationException exception)
        {
            // The provider wraps every Configuration Service transport and deserialization failure in
            // this one type, and its message quotes the configured base address, so only the
            // rejection's type crosses this boundary.
            return Refused(
                request.VerbName,
                "cdcDataStoresUnavailable",
                CdcDiagnosticCategory.StatusObservationUnavailable,
                CdcDiagnosticComponent.ProviderSetup,
                "CDC operation could not load the deployment's data stores from the Configuration "
                    + "Service, so the instance database of the invocation target is unresolvable.",
                exception.GetType().Name,
                timeProvider.GetUtcNow(),
                // A Configuration Service that was momentarily unreachable is worth reissuing
                // against, unlike the sibling refusals that name a fact about the request itself.
                retryable: true
            );
        }
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

    /// <summary>
    /// Moves a target onto a different physical source as a new binding generation.
    /// </summary>
    /// <remarks>
    /// The replacing source carries the same provisioning evidence <c>enable</c> requires - a database
    /// created for this CDC provisioning whose write admission was never opened - because the replacing
    /// generation runs that same initial readiness sequence. V1 defers in-place source reset and topic
    /// reuse, so a replacement is a new generation over a new physical database rather than a retrofit
    /// onto one that already holds rows, and the option surface refuses a caller that cannot attest
    /// both claims.
    ///
    /// The generation being superseded is the operator's own <c>--previous-generation</c> and is never
    /// inferred from what exists. The option surface requires it for this verb, so an absent value here
    /// is a request that was built without one rather than a caller omission; it is refused for itself
    /// rather than defaulted, because the value names the generation whose connector gets fenced.
    /// </remarks>
    private async Task<DocumentCacheAdminCdcCommandResult> ReplaceSourceAsync(
        Invocation invocation,
        CancellationToken cancellationToken
    )
    {
        if (invocation.Request.PreviousGeneration is not { } previousGeneration)
        {
            return Refused(
                invocation.Request.VerbName,
                "cdcSourceReplacementPreviousGenerationMissing",
                CdcDiagnosticCategory.MissingRequiredField,
                CdcDiagnosticComponent.Binding,
                "CDC source replacement requires the generation it supersedes, which is named "
                    + "explicitly and never inferred from the generations that exist.",
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
                AdoptedGovernedNames(adoptedBinding)
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

    /// <summary>
    /// The governed names an adoption operated on, recovered from the binding record the operator
    /// supplied rather than rendered from configuration.
    /// </summary>
    /// <remarks>
    /// Adoption is the one verb whose artifact identity is not the configured one: the record carries
    /// its own generation and instance key, and the controller recovers the names it verifies against
    /// from that record. Rendering the configured identity here would print names for artifacts the
    /// adoption never touched while the JSON contract carried the right ones.
    /// </remarks>
    private static DocumentCacheAdminCdcGovernedNames? AdoptedGovernedNames(CdcBinding adoptedBinding)
    {
        CdcArtifactNameResult names = CdcArtifactNameGenerator.RecoverFromBinding(adoptedBinding);

        return names.Inventory is not { } inventory
            ? null
            : new DocumentCacheAdminCdcGovernedNames(
                inventory.ConnectorName,
                LowerCamel(adoptedBinding.Provider.ToString()),
                adoptedBinding.DataStoreId,
                inventory.InstanceKey,
                inventory.TopicName,
                inventory.ProgressTopicName,
                inventory.SchemaHistoryTopicName
            );
    }

    /// <summary>
    /// One refusal the dispatcher decides for itself, before any controller is entered.
    /// </summary>
    /// <param name="retryable">
    /// Whether reissuing the same invocation could succeed without the operator changing anything.
    /// False for the refusals that name a fact about the request; true only where the refusal names a
    /// dependency that was momentarily unreachable.
    /// </param>
    private static DocumentCacheAdminCdcCommandResult Refused(
        string verbName,
        string code,
        CdcDiagnosticCategory category,
        CdcDiagnosticComponent component,
        string message,
        string observed,
        DateTimeOffset now,
        bool retryable = false
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
                    retryable,
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
        )
        {
            ConnectorAlreadyAbsent = invocation.Request.ConnectorAlreadyAbsent,
        };

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
