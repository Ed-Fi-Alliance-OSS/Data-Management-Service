// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The consumer error codes added with <see cref="JobServiceCollectionExtensions.AddJobErrorCode"/> (spec D-7). A code
/// is an ASCII letter then up to 63 letters or digits; its message is 1 to 1000 characters with no control character
/// and no <c>{</c> or <c>}</c>, so it is fixed text, never a format with placeholders. Infrastructure codes cannot be
/// redefined and a code cannot be added twice; every violation throws, so it fails startup.
/// </summary>
internal sealed partial class JobErrorCodeRegistrations
{
    public const int MaxMessageLength = 1000;

    private readonly Dictionary<string, JobErrorCode> _byCode = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JobErrorCode> ByCode => _byCode;

    public void Add(string code, string message)
    {
        if (code is null || !CodePattern().IsMatch(code))
        {
            throw new ArgumentException(
                "A job error code must be an ASCII letter followed by up to 63 ASCII letters or digits.",
                nameof(code)
            );
        }

        if (
            string.IsNullOrWhiteSpace(message)
            || message.Length > MaxMessageLength
            || message.Any(char.IsControl)
            || message.Contains('{')
            || message.Contains('}')
        )
        {
            throw new ArgumentException(
                $"The message of job error code '{code}' must be 1 to {MaxMessageLength} characters of fixed text, with no "
                    + "control characters and no '{' or '}'.",
                nameof(message)
            );
        }

        if (JobErrorCode.Infrastructure.Any(infrastructure => infrastructure.Code == code))
        {
            throw new InvalidOperationException(
                $"Job error code '{code}' is an infrastructure code and cannot be redefined."
            );
        }

        if (!_byCode.TryAdd(code, new JobErrorCode(code, message)))
        {
            throw new InvalidOperationException($"Job error code '{code}' is registered more than once.");
        }
    }

    // \z rather than $: $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>The infrastructure codes plus the consumer codes registered at startup (spec D-7).</summary>
internal sealed class JobErrorCodeRegistry(JobErrorCodeRegistrations registrations) : IJobErrorCodeRegistry
{
    private readonly Dictionary<string, JobErrorCode> _codes = new(
        JobErrorCode
            .Infrastructure.Concat(registrations.ByCode.Values)
            .Select(errorCode => KeyValuePair.Create(errorCode.Code, errorCode)),
        StringComparer.Ordinal
    );

    public bool TryGet(string code, [NotNullWhen(true)] out JobErrorCode? errorCode) =>
        _codes.TryGetValue(code, out errorCode);
}
