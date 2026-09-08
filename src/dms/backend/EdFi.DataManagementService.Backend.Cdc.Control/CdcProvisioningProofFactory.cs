// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// The evidence an operator supplies when enabling CDC on a database created for it. Each field is a
/// token the caller passed in, never a fact the control plane worked out for itself: whether the
/// database was created for this provisioning, and whether write admission has been closed since it
/// was created, are things only the operator running the enablement can attest to.
/// </summary>
/// <remarks>
/// Every field is nullable because absent evidence is a state this factory must be able to see and
/// refuse. A shape that could only express complete evidence would make the refusal unreachable.
/// </remarks>
public sealed record CdcProvisioningProofEvidence(
    string? SetupControllerRunId,
    string? DatabaseCreationMode,
    string? WriteAdmissionState
);

/// <summary>
/// Issues the provisioning proof the initial-enablement gate is evaluated against.
/// </summary>
/// <remarks>
/// The proof is issued from the caller's evidence alone. The controller never infers that it created
/// the database it is about to capture, and never infers that write admission was closed: both are
/// the assertions the proof exists to record, so inferring either would leave the gate proving only
/// that the control plane ran.
/// </remarks>
public static class CdcProvisioningProofFactory
{
    /// <summary>
    /// The exact token asserting the physical database was created for this CDC provisioning. It is
    /// matched exactly, and case-sensitively, so a near miss is refused rather than read as consent.
    /// </summary>
    public const string CreatedForInitialCdcProvisioningToken = "created-for-initial-cdc-provisioning";

    /// <summary>
    /// The exact token asserting write admission has been closed since the database was created and
    /// has never been opened.
    /// </summary>
    public const string ClosedNeverOpenedToken = "closed-never-opened";

    /// <summary>
    /// Distinguishes the proof from the run that issued it while keeping the two traceable to each
    /// other. The proof identifies one run's evidence, so it is derived from the run rather than drawn
    /// at random.
    /// </summary>
    private const string ProofIdSuffix = ".proof";

    /// <summary>The shared contract's bound on an observation token, which the proof id must fit.</summary>
    private const int MaximumTokenLength = 128;

    /// <summary>
    /// Issues a proof, or reports why none could be issued. A refusal is never partial: the result
    /// carries either a proof that has passed <see cref="InitialCdcProvisioningProofValidator"/> or
    /// the diagnostics explaining what the caller did not supply.
    /// </summary>
    public static CdcContractReadResult<InitialCdcProvisioningProof> Issue(
        CdcObservationContext context,
        CdcProvisioningProofEvidence evidence,
        DateTimeOffset issuedAt
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evidence);

        CdcDiagnosticCollector diagnostics = new(issuedAt);

        string? setupControllerRunId = ValidateRunId(evidence.SetupControllerRunId, issuedAt, diagnostics);
        ValidateExactEvidence(
            evidence.DatabaseCreationMode,
            CreatedForInitialCdcProvisioningToken,
            "$.databaseCreationMode",
            "the database was created for this CDC provisioning",
            issuedAt,
            diagnostics
        );
        ValidateExactEvidence(
            evidence.WriteAdmissionState,
            ClosedNeverOpenedToken,
            "$.writeAdmissionState",
            "write admission has been closed since the database was created",
            issuedAt,
            diagnostics
        );

        if (diagnostics.HasDiagnostics || setupControllerRunId is null)
        {
            return CdcContractReadResult<InitialCdcProvisioningProof>.Failure(diagnostics.Diagnostics);
        }

        InitialCdcProvisioningProof proof = new(
            CdcJsonContract.CurrentContractVersion,
            $"{setupControllerRunId}{ProofIdSuffix}",
            context.OperationId,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            setupControllerRunId,
            CdcDatabaseCreationMode.CreatedForInitialCdcProvisioning,
            CdcWriteAdmissionState.ClosedNeverOpened,
            issuedAt
        );

        // A proof that cannot pass its own contract is not issued at all. The gate reads the proof as
        // the operator's assertion, so an unusable one must be absent rather than present and invalid.
        CdcContractValidationResult validation = InitialCdcProvisioningProofValidator.Validate(
            proof,
            context.ToValidationContext(issuedAt)
        );

        return validation.Succeeded
            ? CdcContractReadResult<InitialCdcProvisioningProof>.Success(proof)
            : CdcContractReadResult<InitialCdcProvisioningProof>.Failure(validation.Diagnostics);
    }

    /// <summary>
    /// Checks the run id here rather than leaving it to the composed proof, because the proof id is
    /// derived from it: an unusable run id would otherwise be reported as an unusable proof id, which
    /// names nothing the caller passed in.
    /// </summary>
    private static string? ValidateRunId(
        string? setupControllerRunId,
        DateTimeOffset issuedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (setupControllerRunId is null || setupControllerRunId.Length == 0)
        {
            diagnostics.Add(
                EvidenceRefused(
                    "provisioningProofRunIdMissing",
                    "$.setupControllerRunId",
                    "CDC provisioning proof requires the setup controller run it is issued for.",
                    "the setup controller run id",
                    "absent",
                    issuedAt
                )
            );
            return null;
        }

        if (
            !CdcKafkaSafeTokenValidator.IsValid(setupControllerRunId)
            || setupControllerRunId.Length + ProofIdSuffix.Length > MaximumTokenLength
        )
        {
            diagnostics.Add(
                EvidenceRefused(
                    "provisioningProofRunIdUnusable",
                    "$.setupControllerRunId",
                    "CDC provisioning proof setup controller run id must be a safe token the proof id "
                        + "can be derived from.",
                    $"a safe token of at most {MaximumTokenLength - ProofIdSuffix.Length} characters",
                    "unusable",
                    issuedAt
                )
            );
            return null;
        }

        return setupControllerRunId;
    }

    private static void ValidateExactEvidence(
        string? supplied,
        string expectedToken,
        string path,
        string assertion,
        DateTimeOffset issuedAt,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (string.Equals(supplied, expectedToken, StringComparison.Ordinal))
        {
            return;
        }

        diagnostics.Add(
            EvidenceRefused(
                supplied is null ? "provisioningEvidenceMissing" : "provisioningEvidenceRejected",
                path,
                $"CDC provisioning proof requires the operator to assert that {assertion}.",
                expectedToken,
                // The supplied value is the caller's own text, so only whether it was recognized
                // crosses this boundary.
                supplied is null
                    ? "absent"
                    : "unrecognized",
                issuedAt
            )
        );
    }

    private static CdcDiagnostic EvidenceRefused(
        string code,
        string path,
        string message,
        string expected,
        string observed,
        DateTimeOffset issuedAt
    ) =>
        new CdcDiagnostic(
            code,
            CdcDiagnosticCategory.MalformedProof,
            CdcDiagnosticSeverity.Error,
            CdcDiagnosticComponent.ProofValidation,
            issuedAt,
            message,
            retryable: false,
            artifactKind: "provisioningProof",
            expected: expected,
            observed: observed
        ).WithPath(path);
}
