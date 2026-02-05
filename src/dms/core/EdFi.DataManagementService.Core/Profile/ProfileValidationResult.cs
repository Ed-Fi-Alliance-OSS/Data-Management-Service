// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Severity levels for profile validation failures.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// Error severity - prevents the profile from being loaded.
    /// </summary>
    Error,

    /// <summary>
    /// Warning severity - allows the profile to be loaded but logs the issue.
    /// </summary>
    Warning,
}

/// <summary>
/// Represents a single validation failure in a profile.
/// </summary>
/// <param name="Severity">The severity of the validation failure.</param>
/// <param name="ProfileName">The name of the profile where the failure occurred.</param>
/// <param name="ResourceName">The name of the resource where the failure occurred, if applicable.</param>
/// <param name="MemberName">The name of the member where the failure occurred, if applicable.</param>
/// <param name="Message">A descriptive message explaining the validation failure.</param>
public record ValidationFailure(
    ValidationSeverity Severity,
    string ProfileName,
    string? ResourceName,
    string? MemberName,
    string Message
);

/// <summary>
/// The result of validating a profile definition against the API schema.
/// </summary>
/// <param name="Failures">The list of validation failures found during validation.</param>
public record ProfileValidationResult(IReadOnlyList<ValidationFailure> Failures)
{
    /// <summary>
    /// Gets whether the validation found any errors (severity Error).
    /// </summary>
    public bool HasErrors => Failures.Any(f => f.Severity == ValidationSeverity.Error);

    /// <summary>
    /// Gets whether the validation found any warnings (severity Warning).
    /// </summary>
    public bool HasWarnings => Failures.Any(f => f.Severity == ValidationSeverity.Warning);

    /// <summary>
    /// Gets whether the validation passed (no errors). Warnings do not affect validity.
    /// </summary>
    public bool IsValid => !HasErrors;

    /// <summary>
    /// Creates a successful validation result with no failures.
    /// </summary>
    public static ProfileValidationResult Success => new([]);

    /// <summary>
    /// Creates a validation result with a single failure.
    /// </summary>
    public static ProfileValidationResult Failure(ValidationFailure failure) => new([failure]);

    /// <summary>
    /// Creates a validation result with multiple failures.
    /// </summary>
    public static ProfileValidationResult Failure(IEnumerable<ValidationFailure> failures) =>
        new(failures.ToList());
}
