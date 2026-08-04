// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
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
        Diagnostics = diagnostics ?? [];
    }

    public CdcSourceFingerprint? ObservedSourceFingerprint { get; }

    public IReadOnlyList<CdcProviderArtifactObservation> ArtifactInventory { get; }

    public IReadOnlyList<CdcGrantObservation> GrantInventory { get; }

    public IReadOnlyList<CdcSourceTableInventory> SourceTableInventory { get; }

    public IReadOnlyList<CdcExpectedMessageKeyColumns> ExpectedMessageKeyColumns { get; }

    public CdcHeartbeatActionQuery? HeartbeatActionQuery { get; }

    public IReadOnlyList<CdcProviderHistoryObservation> ProviderHistoryObservations { get; }

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
            AddDiagnosticForSourceFingerprintMismatch(stepResult.ObservedSourceFingerprint);
        }

        UpsertArtifactObservations(stepResult.ArtifactInventory);
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
            AddDiagnosticForBindingArtifactNameMismatch(observation);
        }

        foreach (var grant in stepResult.GrantInventory)
        {
            AddDiagnosticsForGrantMismatch(grant);
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

    private void UpsertArtifactObservations(
        IReadOnlyList<CdcProviderArtifactObservation> artifactObservations
    )
    {
        foreach (var observation in artifactObservations)
        {
            var existingIndex = _artifactInventory.FindIndex(existing =>
                existing.ArtifactKind == observation.ArtifactKind
                && existing.SafeArtifactName.Equals(observation.SafeArtifactName)
            );
            if (existingIndex < 0)
            {
                _artifactInventory.Add(observation);
                continue;
            }

            var existing = _artifactInventory[existingIndex];
            var state =
                existing.State == CdcProviderArtifactState.Created
                && observation.State == CdcProviderArtifactState.Matched
                    ? CdcProviderArtifactState.Created
                    : observation.State;
            _artifactInventory[existingIndex] = observation with { State = state };
        }
    }

    public CdcProviderSetupResult ToResult()
    {
        AddDiagnosticsForCompletedBindingValidation();

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

        if (!request.ArtifactOutput.ShouldCreateManifestPayload)
        {
            return result;
        }

        var providerOptInEnabled = outcome != CdcProviderSetupOutcome.Failed;
        var manifestPayload = CdcProviderManifestEmitter.CreatePayload(result);
        result = result with { ManifestPayload = manifestPayload };

        if (request.ArtifactOutput.ManifestOutputDirectoryPath is null)
        {
            return result;
        }

        var outputFailure = CdcProviderArtifactOutputWriter.WriteManifestPayload(
            request.ArtifactOutput.ManifestOutputDirectoryPath,
            manifestPayload
        );
        if (outputFailure is null)
        {
            return result;
        }

        var failedResult = result with
        {
            Outcome = CdcProviderSetupOutcome.Failed,
            Diagnostics = [.. result.Diagnostics, outputFailure],
        };

        return failedResult with
        {
            ManifestPayload = CdcProviderManifestEmitter.CreatePayload(failedResult, providerOptInEnabled),
        };
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

    private void AddDiagnosticForSourceFingerprintMismatch(CdcSourceFingerprint observedSourceFingerprint)
    {
        if (observedSourceFingerprint == request.BoundPhysicalSourceFingerprint)
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_SOURCE_FINGERPRINT_MISMATCH",
                Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: CdcProviderArtifactKind.SourceFingerprint,
                SafeName: CdcSourceFingerprintMetadata.SafeArtifactName,
                ExpectedValue: FingerprintValue(request.BoundPhysicalSourceFingerprint),
                ObservedValue: FingerprintValue(observedSourceFingerprint),
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticForBindingArtifactNameMismatch(CdcProviderArtifactObservation observation)
    {
        if (_observedSourceFingerprint is null)
        {
            return;
        }

        var expectedNames = ExpectedBindingArtifactNames(observation.ArtifactKind);
        if (expectedNames.Count == 0 || expectedNames.Contains(observation.SafeArtifactName.Value))
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_ARTIFACT_NAME_MISMATCH",
                Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: observation.ArtifactKind,
                SafeName: observation.SafeArtifactName,
                ExpectedValue: Csv(expectedNames),
                ObservedValue: SafeDiagnosticValue(observation.SafeArtifactName.Value),
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticsForGrantMismatch(CdcGrantObservation grant)
    {
        if (
            grant.PrincipalKind == CdcPrincipalKind.ConnectorPrincipal
            && !grant.SafePrincipalName.Equals(request.ConnectorPrincipal.SafePrincipalName)
        )
        {
            AddDiagnosticIfMissing(
                new CdcProviderDiagnostic(
                    Code: "CDC_BINDING_CONNECTOR_PRINCIPAL_MISMATCH",
                    Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.Grant,
                    SafeName: grant.SafeObjectName,
                    ExpectedValue: SafeDiagnosticValue(request.ConnectorPrincipal.SafePrincipalName.Value),
                    ObservedValue: SafeDiagnosticValue(grant.SafePrincipalName.Value),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        var workTableName = SafeObjectName(DmsTableNames.DocumentProjectionWork);
        if (string.Equals(grant.SafeObjectName.Value, workTableName, StringComparison.Ordinal))
        {
            AddDiagnosticIfMissing(
                new CdcProviderDiagnostic(
                    Code: "CDC_BINDING_WORK_TABLE_GRANT_FORBIDDEN",
                    Category: CdcProviderDiagnosticCategory.WorkTableGrantViolation,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.Grant,
                    SafeName: grant.SafeObjectName,
                    ExpectedValue: "no-access",
                    ObservedValue: Csv(grant.Privileges),
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        AddDiagnosticsForSourceTableGrantMismatch(grant);
        AddDiagnosticsForHeartbeatGrantMismatch(grant);
        AddDiagnosticsForSqlServerGatingRoleGrantMismatch(grant);
    }

    private void AddDiagnosticsForSourceTableGrantMismatch(CdcGrantObservation grant)
    {
        var sourceTableNames = new[]
        {
            SafeObjectName(DmsTableNames.Document),
            SafeObjectName(DmsTableNames.DocumentCache),
        };

        if (!sourceTableNames.Contains(grant.SafeObjectName.Value))
        {
            return;
        }

        var unexpectedPrivileges = grant
            .Privileges.Where(privilege => !string.Equals(privilege, "SELECT", StringComparison.Ordinal))
            .ToArray();
        if (unexpectedPrivileges.Length == 0)
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_SOURCE_TABLE_GRANT_MISMATCH",
                Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                ArtifactKind: CdcProviderArtifactKind.Grant,
                SafeName: grant.SafeObjectName,
                ExpectedValue: "SELECT-only",
                ObservedValue: Csv(unexpectedPrivileges),
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticsForHeartbeatGrantMismatch(CdcGrantObservation grant)
    {
        if (!string.Equals(grant.SafeObjectName.Value, SafeObjectName(DmsTableNames.CdcHeartbeat)))
        {
            return;
        }

        var unexpectedUpdateColumns = grant
            .Columns.Select(column => column.Value)
            .Where(column =>
                !string.Equals(column, "HeartbeatSequence", StringComparison.Ordinal)
                && !string.Equals(column, "HeartbeatAt", StringComparison.Ordinal)
            )
            .ToArray();
        if (unexpectedUpdateColumns.Length == 0)
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_HEARTBEAT_GRANT_MISMATCH",
                Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                ArtifactKind: CdcProviderArtifactKind.Grant,
                SafeName: grant.SafeObjectName,
                ExpectedValue: "HeartbeatSequence,HeartbeatAt",
                ObservedValue: Csv(unexpectedUpdateColumns),
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticsForSqlServerGatingRoleGrantMismatch(CdcGrantObservation grant)
    {
        if (request.Provider != CdcProvider.SqlServer || !grant.SafeObjectName.Value.StartsWith("role."))
        {
            return;
        }

        var expectedRoleName = SqlServerGatingRoleGrantObjectName();
        if (string.Equals(grant.SafeObjectName.Value, expectedRoleName, StringComparison.Ordinal))
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_SQLSERVER_GATING_ROLE_NAME_MISMATCH",
                Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                ArtifactKind: CdcProviderArtifactKind.SqlServerGatingRole,
                SafeName: grant.SafeObjectName,
                ExpectedValue: expectedRoleName,
                ObservedValue: SafeDiagnosticValue(grant.SafeObjectName.Value),
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticsForCompletedBindingValidation()
    {
        if (_observedSourceFingerprint is null || HasErrorDiagnostics)
        {
            return;
        }

        AddDiagnosticsForMissingExpectedBindingArtifacts();
        AddDiagnosticsForExpectedMessageKeys();
        AddDiagnosticsForHeartbeatMetadata();
        AddDiagnosticsForMissingSqlServerGatingRoleGrant();
    }

    private void AddDiagnosticsForMissingExpectedBindingArtifacts()
    {
        foreach (var (artifactKind, expectedNames) in ExpectedBindingArtifactNames())
        {
            var observedNames = _artifactInventory
                .Where(observation => observation.ArtifactKind == artifactKind)
                .Select(observation => observation.SafeArtifactName.Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (
                var expectedName in expectedNames.Where(expectedName => !observedNames.Contains(expectedName))
            )
            {
                AddDiagnosticIfMissing(
                    new CdcProviderDiagnostic(
                        Code: "CDC_BINDING_ARTIFACT_MISSING",
                        Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                        Severity: CdcProviderDiagnosticSeverity.Error,
                        PrincipalKind: CdcPrincipalKind.None,
                        ArtifactKind: artifactKind,
                        SafeName: new CdcSafeName(expectedName),
                        ExpectedValue: "observed-binding-artifact",
                        ObservedValue: "missing",
                        ProviderErrorClass: null,
                        Classification: CdcProviderRetryContinuityClassification.FailClosed
                    )
                );
            }
        }
    }

    private void AddDiagnosticsForExpectedMessageKeys()
    {
        var expectedKeys = new Dictionary<CdcSourceTableKind, IReadOnlyList<string>>
        {
            [CdcSourceTableKind.Document] = ["DocumentUuid"],
            [CdcSourceTableKind.DocumentCache] = ["DocumentUuid"],
        };
        var observedByKind = _expectedMessageKeyColumns
            .GroupBy(key => key.TableKind)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var (tableKind, expectedColumnNames) in expectedKeys)
        {
            if (!observedByKind.TryGetValue(tableKind, out var observedKeys))
            {
                AddMessageKeyDiagnostic(
                    "CDC_BINDING_MESSAGE_KEY_COLUMNS_MISSING",
                    tableKind,
                    Csv(expectedColumnNames),
                    "missing"
                );
                continue;
            }

            if (
                observedKeys.Length != 1
                || !observedKeys[0]
                    .KeyColumns.Select(column => column.Value)
                    .SequenceEqual(expectedColumnNames, StringComparer.Ordinal)
            )
            {
                AddMessageKeyDiagnostic(
                    "CDC_BINDING_MESSAGE_KEY_COLUMNS_MISMATCH",
                    tableKind,
                    Csv(expectedColumnNames),
                    Csv(observedKeys.SelectMany(key => key.KeyColumns.Select(column => column.Value)))
                );
            }
        }

        foreach (var unexpectedKind in observedByKind.Keys.Except(expectedKeys.Keys))
        {
            AddMessageKeyDiagnostic(
                "CDC_BINDING_MESSAGE_KEY_COLUMNS_UNEXPECTED",
                unexpectedKind,
                "absent",
                Csv(
                    observedByKind[unexpectedKind]
                        .SelectMany(key => key.KeyColumns.Select(column => column.Value))
                )
            );
        }
    }

    private void AddMessageKeyDiagnostic(
        string code,
        CdcSourceTableKind tableKind,
        string expectedValue,
        string observedValue
    )
    {
        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: code,
                Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: CdcProviderArtifactKind.SourceColumn,
                SafeName: SourceTableSafeName(tableKind),
                ExpectedValue: expectedValue,
                ObservedValue: observedValue,
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticsForHeartbeatMetadata()
    {
        var heartbeatName = SafeObjectName(DmsTableNames.CdcHeartbeat);
        var heartbeatObserved = _artifactInventory.Exists(observation =>
            observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
            && string.Equals(observation.SafeArtifactName.Value, heartbeatName, StringComparison.Ordinal)
        );

        if (!heartbeatObserved)
        {
            AddDiagnosticIfMissing(
                new CdcProviderDiagnostic(
                    Code: "CDC_BINDING_HEARTBEAT_ARTIFACT_MISSING",
                    Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.HeartbeatTable,
                    SafeName: new CdcSafeName(heartbeatName),
                    ExpectedValue: "observed-heartbeat-artifact",
                    ObservedValue: "missing",
                    ProviderErrorClass: null,
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                )
            );
        }

        if (_heartbeatActionQuery is not null)
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_HEARTBEAT_ACTION_QUERY_MISSING",
                Category: CdcProviderDiagnosticCategory.ValidationMismatch,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.None,
                ArtifactKind: CdcProviderArtifactKind.HeartbeatActionQuery,
                SafeName: new CdcSafeName(heartbeatName),
                ExpectedValue: "provider-generated-query",
                ObservedValue: "missing",
                ProviderErrorClass: null,
                Classification: CdcProviderRetryContinuityClassification.FailClosed
            )
        );
    }

    private void AddDiagnosticsForMissingSqlServerGatingRoleGrant()
    {
        if (request.Provider != CdcProvider.SqlServer)
        {
            return;
        }

        var expectedRoleName = SqlServerGatingRoleGrantObjectName();
        if (
            _grantInventory.Exists(grant =>
                string.Equals(grant.SafeObjectName.Value, expectedRoleName, StringComparison.Ordinal)
                && grant.Privileges.Contains("MEMBER", StringComparer.Ordinal)
            )
        )
        {
            return;
        }

        AddDiagnosticIfMissing(
            new CdcProviderDiagnostic(
                Code: "CDC_BINDING_SQLSERVER_GATING_ROLE_GRANT_MISSING",
                Category: CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure,
                Severity: CdcProviderDiagnosticSeverity.Error,
                PrincipalKind: CdcPrincipalKind.ConnectorPrincipal,
                ArtifactKind: CdcProviderArtifactKind.SqlServerGatingRole,
                SafeName: new CdcSafeName(expectedRoleName),
                ExpectedValue: "connector-role-membership",
                ObservedValue: "missing",
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

    private IReadOnlyList<string> ExpectedBindingArtifactNames(CdcProviderArtifactKind artifactKind) =>
        ExpectedBindingArtifactNames().TryGetValue(artifactKind, out var names) ? names : [];

    private IReadOnlyDictionary<CdcProviderArtifactKind, IReadOnlyList<string>> ExpectedBindingArtifactNames()
    {
        return request.Provider switch
        {
            CdcProvider.Postgresql => new Dictionary<CdcProviderArtifactKind, IReadOnlyList<string>>
            {
                [CdcProviderArtifactKind.PostgresqlPublication] =
                [
                    request.ArtifactNames.Postgresql!.PublicationName.Value,
                ],
                [CdcProviderArtifactKind.PostgresqlReplicationSlot] =
                [
                    request.ArtifactNames.Postgresql!.ReplicationSlotName.Value,
                ],
            },
            CdcProvider.SqlServer => new Dictionary<CdcProviderArtifactKind, IReadOnlyList<string>>
            {
                [CdcProviderArtifactKind.SqlServerGatingRole] =
                [
                    request.ArtifactNames.SqlServer!.GatingRoleName.Value,
                ],
                [CdcProviderArtifactKind.SqlServerCaptureInstance] = request
                    .ArtifactNames.SqlServer.CaptureInstanceNames.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value.Value)
                    .ToArray(),
            },
            _ => throw new InvalidOperationException($"Unsupported CDC provider {request.Provider}."),
        };
    }

    private string SqlServerGatingRoleGrantObjectName() =>
        $"role.{SafeDiagnosticValue(request.ArtifactNames.SqlServer!.GatingRoleName.Value)}";

    private CdcSafeName SourceTableSafeName(CdcSourceTableKind tableKind)
    {
        var table = request.ExpectedSourceInventory.FirstOrDefault(table => table.TableKind == tableKind);
        return table is null
            ? new CdcSafeName(tableKind.ToString())
            : new CdcSafeName(SafeObjectName(table.TableName));
    }

    private static string SafeObjectName(DbTableName table) =>
        $"{SafeDiagnosticValue(table.Schema.Value)}.{SafeDiagnosticValue(table.Name)}";

    private static string FingerprintValue(CdcSourceFingerprint fingerprint) =>
        $"{SafeDiagnosticValue(fingerprint.Version)}:{SafeDiagnosticValue(fingerprint.Value)}";

    private static string Csv(IEnumerable<string> values) =>
        string.Join(",", values.Select(SafeDiagnosticValue).Order(StringComparer.Ordinal));

    private static string SafeDiagnosticValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var safeCharacters = value.Select(character =>
            char.IsLetterOrDigit(character) || character == '_' || character == '.' ? character : '_'
        );
        return new string(safeCharacters.ToArray());
    }

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
