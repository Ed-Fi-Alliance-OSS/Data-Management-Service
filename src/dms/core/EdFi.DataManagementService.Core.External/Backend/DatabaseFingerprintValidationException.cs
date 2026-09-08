// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Thrown when the provisioned dms.EffectiveSchema content is present but malformed.
/// This represents a per-database fail-fast condition rather than a transient read failure.
/// </summary>
public sealed class DatabaseFingerprintValidationException : InvalidOperationException
{
    public DatabaseFingerprintValidationException(string message)
        : this(CreateSingleValidationIssue(message), null) { }

    public DatabaseFingerprintValidationException(string message, Exception innerException)
        : this(CreateSingleValidationIssue(message), innerException) { }

    public DatabaseFingerprintValidationException(IEnumerable<string> validationIssues)
        : this(CreateValidationIssues(validationIssues), null) { }

    public IReadOnlyList<string> ValidationIssues { get; }

    private DatabaseFingerprintValidationException(string[] validationIssues, Exception? innerException)
        : base(validationIssues[0], innerException)
    {
        ValidationIssues = validationIssues;
    }

    private static string[] CreateSingleValidationIssue(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return [message];
    }

    private static string[] CreateValidationIssues(IEnumerable<string> validationIssues)
    {
        ArgumentNullException.ThrowIfNull(validationIssues);

        string[] issues = [.. validationIssues.Select(ValidateIssue)];

        if (issues.Length == 0)
        {
            throw new ArgumentException(
                "At least one validation issue is required.",
                nameof(validationIssues)
            );
        }

        return issues;
    }

    private static string ValidateIssue(string issue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issue);
        return issue;
    }
}
