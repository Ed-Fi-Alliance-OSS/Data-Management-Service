// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdFi.DataManagementService.Backend.Ddl;

internal interface ICdcProviderSetupService
{
    Task<CdcProviderSetupResult> SetupAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken = default
    );
}

internal interface ICdcProviderSetupProvider
{
    CdcProvider Provider { get; }

    IReadOnlyList<CdcProviderSetupStep> BuildSetupSteps(CdcProviderSetupRequest request);
}

internal enum CdcProviderSetupStepMode
{
    ExactMatchOnly,
    CreateOrExactMatch,
}

internal sealed record CdcProviderSetupStepContext(
    CdcProviderSetupRequest Request,
    CdcProviderSetupStepMode Mode
);

internal delegate Task<CdcProviderSetupStepResult> CdcProviderSetupStepExecutor(
    CdcProviderSetupStepContext context,
    CancellationToken cancellationToken
);

internal sealed class CdcProviderSetupStep
{
    public CdcProviderSetupStep(
        CdcProviderArtifactKind artifactKind,
        CdcSafeName safeName,
        bool canCreateInInitialSetup,
        CdcProviderSetupStepExecutor executeAsync
    )
    {
        ArgumentNullException.ThrowIfNull(executeAsync);

        ArtifactKind = artifactKind;
        SafeName = safeName;
        CanCreateInInitialSetup = canCreateInInitialSetup;
        ExecuteAsync = executeAsync;
    }

    public CdcProviderArtifactKind ArtifactKind { get; }

    public CdcSafeName SafeName { get; }

    public bool CanCreateInInitialSetup { get; }

    public CdcProviderSetupStepExecutor ExecuteAsync { get; }
}

internal sealed record CdcProviderSetupStepResult
{
    public CdcProviderSetupStepResult(
        CdcSourceFingerprint? observedSourceFingerprint = null,
        IReadOnlyList<CdcProviderArtifactObservation>? artifactInventory = null,
        IReadOnlyList<CdcGrantObservation>? grantInventory = null,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        CdcHeartbeatActionQuery? heartbeatActionQuery = null,
        IReadOnlyList<CdcProviderHistoryObservation>? providerHistoryObservations = null,
        CdcProviderManifestPayload? manifestPayload = null,
        IReadOnlyList<CdcProviderDiagnostic>? diagnostics = null
    )
    {
        ObservedSourceFingerprint = observedSourceFingerprint;
        ArtifactInventory = artifactInventory ?? [];
        GrantInventory = grantInventory ?? [];
        SourceTableInventory = sourceTableInventory ?? [];
        ExpectedMessageKeyColumns = expectedMessageKeyColumns ?? [];
        HeartbeatActionQuery = heartbeatActionQuery;
        ProviderHistoryObservations = providerHistoryObservations ?? [];
        ManifestPayload = manifestPayload;
        Diagnostics = diagnostics ?? [];
    }

    public CdcSourceFingerprint? ObservedSourceFingerprint { get; }

    public IReadOnlyList<CdcProviderArtifactObservation> ArtifactInventory { get; }

    public IReadOnlyList<CdcGrantObservation> GrantInventory { get; }

    public IReadOnlyList<CdcSourceTableInventory> SourceTableInventory { get; }

    public IReadOnlyList<CdcExpectedMessageKeyColumns> ExpectedMessageKeyColumns { get; }

    public CdcHeartbeatActionQuery? HeartbeatActionQuery { get; }

    public IReadOnlyList<CdcProviderHistoryObservation> ProviderHistoryObservations { get; }

    public CdcProviderManifestPayload? ManifestPayload { get; }

    public IReadOnlyList<CdcProviderDiagnostic> Diagnostics { get; }
}

internal sealed class CdcProviderSetupService(IEnumerable<ICdcProviderSetupProvider> providers)
    : ICdcProviderSetupService
{
    private readonly IReadOnlyDictionary<CdcProvider, ICdcProviderSetupProvider> _providers =
        BuildProviderDictionary(providers);

    public async Task<CdcProviderSetupResult> SetupAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_providers.TryGetValue(request.Provider, out var provider))
        {
            return BuildFailedResult(
                request,
                new CdcProviderDiagnostic(
                    Code: "CDC_PROVIDER_SETUP_PROVIDER_MISSING",
                    Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.None,
                    SafeName: new CdcSafeName(request.Provider.ToString()),
                    ExpectedValue: "registered-provider",
                    ObservedValue: "missing",
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        var steps = provider.BuildSetupSteps(request);
        ArgumentNullException.ThrowIfNull(steps);

        var aggregate = new CdcProviderSetupAggregate(request);

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepMode = DetermineStepMode(request.Mode, step);
            var stepResult = await step.ExecuteAsync(
                    new CdcProviderSetupStepContext(request, stepMode),
                    cancellationToken
                )
                .ConfigureAwait(false);

            aggregate.AddStepResult(step, stepMode, stepResult);

            if (aggregate.HasErrorDiagnostics)
            {
                break;
            }
        }

        return aggregate.ToResult();
    }

    private static CdcProviderSetupStepMode DetermineStepMode(
        CdcProviderSetupMode mode,
        CdcProviderSetupStep step
    ) =>
        mode switch
        {
            CdcProviderSetupMode.InitialCreateOrExactMatch => step.CanCreateInInitialSetup
                ? CdcProviderSetupStepMode.CreateOrExactMatch
                : CdcProviderSetupStepMode.ExactMatchOnly,
            CdcProviderSetupMode.ValidateOnly => CdcProviderSetupStepMode.ExactMatchOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported CDC setup mode."),
        };

    private static IReadOnlyDictionary<CdcProvider, ICdcProviderSetupProvider> BuildProviderDictionary(
        IEnumerable<ICdcProviderSetupProvider> providers
    )
    {
        ArgumentNullException.ThrowIfNull(providers);

        Dictionary<CdcProvider, ICdcProviderSetupProvider> byProvider = [];
        foreach (var provider in providers)
        {
            if (!byProvider.TryAdd(provider.Provider, provider))
            {
                throw new InvalidOperationException(
                    $"Multiple CDC setup providers were registered for {provider.Provider}."
                );
            }
        }

        return byProvider;
    }

    private static CdcProviderSetupResult BuildFailedResult(
        CdcProviderSetupRequest request,
        CdcProviderDiagnostic diagnostic
    ) =>
        new(
            Provider: request.Provider,
            Mode: request.Mode,
            Outcome: CdcProviderSetupOutcome.Failed,
            BoundPhysicalSourceFingerprint: request.BoundPhysicalSourceFingerprint,
            ObservedSourceFingerprint: null,
            ArtifactInventory: [],
            GrantInventory: [],
            SourceTableInventory: [],
            ExpectedMessageKeyColumns: [],
            HeartbeatActionQuery: null,
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: [diagnostic]
        );
}

internal sealed class CdcProviderSetupAggregate(CdcProviderSetupRequest request)
{
    private readonly List<CdcProviderArtifactObservation> _artifactInventory = [];
    private readonly List<CdcGrantObservation> _grantInventory = [];
    private readonly List<CdcSourceTableInventory> _sourceTableInventory = [];
    private readonly List<CdcExpectedMessageKeyColumns> _expectedMessageKeyColumns = [];
    private readonly List<CdcProviderHistoryObservation> _providerHistoryObservations = [];
    private readonly List<CdcProviderDiagnostic> _diagnostics = [];
    private CdcSourceFingerprint? _observedSourceFingerprint;
    private CdcHeartbeatActionQuery? _heartbeatActionQuery;

    public bool HasErrorDiagnostics =>
        _diagnostics.Exists(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);

    public void AddStepResult(
        CdcProviderSetupStep step,
        CdcProviderSetupStepMode stepMode,
        CdcProviderSetupStepResult stepResult
    )
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(stepResult);

        if (stepResult.ObservedSourceFingerprint is not null)
        {
            _observedSourceFingerprint = stepResult.ObservedSourceFingerprint;
        }

        _artifactInventory.AddRange(stepResult.ArtifactInventory);
        _grantInventory.AddRange(stepResult.GrantInventory);
        _sourceTableInventory.AddRange(stepResult.SourceTableInventory);
        _expectedMessageKeyColumns.AddRange(stepResult.ExpectedMessageKeyColumns);
        UpsertProviderHistoryObservations(stepResult.ProviderHistoryObservations);
        _diagnostics.AddRange(stepResult.Diagnostics);

        _heartbeatActionQuery ??= stepResult.HeartbeatActionQuery;

        if (stepResult.SourceTableInventory.Count > 0)
        {
            _diagnostics.AddRange(
                CdcSourceInventoryValidator.ValidateLiveSourceInventory(
                    request.ExpectedSourceInventory,
                    stepResult.SourceTableInventory
                )
            );
        }

        foreach (var observation in stepResult.ArtifactInventory)
        {
            AddDiagnosticForUnexpectedCreate(step, stepMode, observation);
            AddDiagnosticForUnsafeArtifactState(observation);
        }
    }

    private void UpsertProviderHistoryObservations(
        IReadOnlyList<CdcProviderHistoryObservation> providerHistoryObservations
    )
    {
        foreach (var observation in providerHistoryObservations)
        {
            _providerHistoryObservations.RemoveAll(existing =>
                existing.ArtifactKind == observation.ArtifactKind
                && existing.SafeArtifactName.Equals(observation.SafeArtifactName)
            );
            _providerHistoryObservations.Add(observation);
        }
    }

    public CdcProviderSetupResult ToResult()
    {
        var outcome = DetermineOutcome();

        var result = new CdcProviderSetupResult(
            Provider: request.Provider,
            Mode: request.Mode,
            Outcome: outcome,
            BoundPhysicalSourceFingerprint: request.BoundPhysicalSourceFingerprint,
            ObservedSourceFingerprint: _observedSourceFingerprint,
            ArtifactInventory: _artifactInventory,
            GrantInventory: _grantInventory,
            SourceTableInventory: _sourceTableInventory,
            ExpectedMessageKeyColumns: _expectedMessageKeyColumns,
            HeartbeatActionQuery: _heartbeatActionQuery,
            ProviderHistoryObservations: _providerHistoryObservations,
            ManifestPayload: null,
            Diagnostics: _diagnostics
        );

        return request.ArtifactOutput.IncludeManifestPayload
            ? result with
            {
                ManifestPayload = CdcProviderManifestEmitter.CreatePayload(result),
            }
            : result;
    }

    private CdcProviderSetupOutcome DetermineOutcome()
    {
        if (HasErrorDiagnostics)
        {
            return CdcProviderSetupOutcome.Failed;
        }

        return _artifactInventory.Exists(observation => observation.State == CdcProviderArtifactState.Created)
            ? CdcProviderSetupOutcome.CreatedOrMatched
            : CdcProviderSetupOutcome.ExactMatch;
    }

    private void AddDiagnosticForUnexpectedCreate(
        CdcProviderSetupStep step,
        CdcProviderSetupStepMode stepMode,
        CdcProviderArtifactObservation observation
    )
    {
        if (observation.State != CdcProviderArtifactState.Created)
        {
            return;
        }

        if (stepMode == CdcProviderSetupStepMode.CreateOrExactMatch && step.CanCreateInInitialSetup)
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_PROVIDER_SETUP_UNEXPECTED_CREATE",
                Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: observation.ArtifactKind,
                SafeName: observation.SafeArtifactName,
                ExpectedValue: "exact-match-only",
                ObservedValue: "created",
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticForUnsafeArtifactState(CdcProviderArtifactObservation observation)
    {
        if (observation.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched)
        {
            return;
        }

        var category = observation.ArtifactKind switch
        {
            CdcProviderArtifactKind.PostgresqlReplicationSlot
            or CdcProviderArtifactKind.SqlServerCaptureInstance
            or CdcProviderArtifactKind.ProviderHistory
                when observation.State == CdcProviderArtifactState.Unavailable =>
                CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
            _ => observation.ArtifactKind
                is CdcProviderArtifactKind.SourceTable
                    or CdcProviderArtifactKind.SourceColumn
                ? CdcProviderDiagnosticCategory.MissingRequiredSourceObject
                : CdcProviderDiagnosticCategory.ValidationMismatch,
        };

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: DiagnosticCodeForState(observation.State),
                Category: category,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: observation.ArtifactKind,
                SafeName: observation.SafeArtifactName,
                ExpectedValue: "matched",
                ObservedValue: observation.State.ToString(),
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private static string DiagnosticCodeForState(CdcProviderArtifactState state) =>
        state switch
        {
            CdcProviderArtifactState.Missing => "CDC_PROVIDER_ARTIFACT_MISSING",
            CdcProviderArtifactState.Mismatched => "CDC_PROVIDER_ARTIFACT_MISMATCH",
            CdcProviderArtifactState.Unavailable => "CDC_PROVIDER_ARTIFACT_UNAVAILABLE",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported artifact state."),
        };

    private void AddDiagnosticIfMissing(CdcProviderDiagnostic diagnostic)
    {
        if (
            _diagnostics.Exists(existing =>
                existing.Severity == CdcProviderDiagnosticSeverity.Error
                && existing.ArtifactKind == diagnostic.ArtifactKind
                && existing.SafeName.Equals(diagnostic.SafeName)
            )
        )
        {
            return;
        }

        _diagnostics.Add(diagnostic);
    }
}

internal static class CdcProviderSetupServiceCollectionExtensions
{
    internal static IServiceCollection AddCdcProviderSetupService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(ServiceDescriptor.Scoped<ICdcProviderSetupService, CdcProviderSetupService>());

        return services;
    }

    internal static IServiceCollection AddCdcProviderSetupProvider<TProvider>(
        this IServiceCollection services
    )
        where TProvider : class, ICdcProviderSetupProvider
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Add(ServiceDescriptor.Scoped<ICdcProviderSetupProvider, TProvider>());

        return services;
    }
}
