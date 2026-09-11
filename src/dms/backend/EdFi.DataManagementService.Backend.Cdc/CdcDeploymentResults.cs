// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcDeploymentComponent
{
    Request,
    WorkflowState,
    Projection,
    ProviderSetup,
    Kafka,
    Connect,
    Worker,
    Metrics,
    WriterPublication,
}

public enum CdcDeploymentFailure
{
    InvalidInput,
    Unavailable,
    Timeout,
    AuthenticationFailed,
    Conflict,
    ValidationFailed,
}

/// <summary>Only allow-listed classifications cross the workflow output boundary.</summary>
public sealed class CdcDeploymentDiagnostic
{
    public CdcDeploymentDiagnostic(CdcDeploymentComponent component, CdcDeploymentFailure failure)
    {
        if (!Enum.IsDefined(component) || !Enum.IsDefined(failure))
        {
            throw new ArgumentException("Unknown CDC diagnostic classification.");
        }
        Component = component;
        Failure = failure;
    }

    public CdcDeploymentComponent Component { get; }
    public CdcDeploymentFailure Failure { get; }
    public string Message =>
        Failure switch
        {
            CdcDeploymentFailure.InvalidInput => "Deployment input is invalid.",
            CdcDeploymentFailure.Timeout =>
                "The bounded operation timed out; reconcile live state before retrying.",
            CdcDeploymentFailure.AuthenticationFailed =>
                "Authentication or authorization failed; artifact presence is unknown.",
            CdcDeploymentFailure.Conflict =>
                "The operation conflicted with current state; reconcile before retrying.",
            CdcDeploymentFailure.ValidationFailed => "Observed state does not satisfy the required contract.",
            _ => "Authoritative evidence is unavailable; reconcile live state before retrying.",
        };

    /// <summary>Never include exception text, response bodies, source identifiers, or nested exceptions.</summary>
    public static CdcDeploymentDiagnostic FromException(CdcDeploymentComponent component, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            // Cancellation is control flow, including its original token, not an unavailable observation.
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
        return new(
            component,
            exception switch
            {
                TimeoutException => CdcDeploymentFailure.Timeout,
                UnauthorizedAccessException => CdcDeploymentFailure.AuthenticationFailed,
                HttpRequestException
                {
                    StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                } => CdcDeploymentFailure.AuthenticationFailed,
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict } =>
                    CdcDeploymentFailure.Conflict,
                _ => CdcDeploymentFailure.Unavailable,
            }
        );
    }

    public override string ToString() => $"{Component}: {Failure}";
}

/// <summary>
/// Adapters return Absent only after a successful authoritative lookup proves absence.
/// Lost responses and connection/authentication failures are Unavailable, never Absent.
/// Payloads stay in memory; the controller emits a separately validated workflow result.
/// </summary>
public enum CdcTransportEvidenceState
{
    Observed,
    Absent,
    Unavailable,
}

public abstract class CdcTransportResult<T>
    where T : notnull
{
    private CdcTransportResult() { }

    public abstract CdcTransportEvidenceState State { get; }
    public virtual IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics => [];

    public sealed class Observed(T value) : CdcTransportResult<T>
    {
        public override CdcTransportEvidenceState State => CdcTransportEvidenceState.Observed;

        [JsonIgnore]
        public T Value { get; } = value is not null ? value : throw new ArgumentNullException(nameof(value));

        public override string ToString() => "Observed";
    }

    public sealed class Absent : CdcTransportResult<T>
    {
        public override CdcTransportEvidenceState State => CdcTransportEvidenceState.Absent;

        public override string ToString() => "Absent";
    }

    public sealed class Unavailable : CdcTransportResult<T>
    {
        public Unavailable(CdcDeploymentDiagnostic diagnostic)
            : this([diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))]) { }

        private Unavailable(IReadOnlyList<CdcDeploymentDiagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            if (diagnostics.Count == 0 || diagnostics.Any(d => d is null))
            {
                throw new ArgumentException("At least one CDC diagnostic is required.", nameof(diagnostics));
            }
            Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        }

        // A named factory preserves existing target-typed single-diagnostic constructor calls.
        public static Unavailable FromDiagnostics(IReadOnlyList<CdcDeploymentDiagnostic> diagnostics) =>
            new(diagnostics);

        public override CdcTransportEvidenceState State => CdcTransportEvidenceState.Unavailable;
        public override IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics { get; }
        public CdcDeploymentDiagnostic Diagnostic => Diagnostics[0];

        public override string ToString() => "Unavailable";
    }
}
