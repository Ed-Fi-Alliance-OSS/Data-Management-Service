// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The consumer error codes added with <see cref="JobServiceCollectionExtensions.AddJobErrorCode"/> (spec D-7). A code
/// is an ASCII letter then up to 63 letters or digits; its message is 1 to 1000 characters with no control character
/// and no <c>{</c> or <c>}</c>, so it is fixed text, never a format with placeholders. Infrastructure codes cannot be
/// redefined and a code cannot be added twice; every violation throws, so it fails startup.
/// </summary>
/// <remarks>
/// Composite codes, added with <see cref="JobServiceCollectionExtensions.AddJobCompositeErrorCode"/>, follow the same
/// code and message rules and share the namespace of single codes. Each lists the category codes a failure may name:
/// every category must already be registered as a single code, consumer or infrastructure, and appear once. The
/// longest text a failure could compose, the composite's message followed by every category code, must fit in
/// <see cref="MaxMessageLength"/> characters. These checks run when the composite is added, so a violation fails
/// startup whether or not job workers run.
/// </remarks>
internal sealed partial class JobErrorCodeRegistrations
{
    public const int MaxMessageLength = 1000;

    /// <summary>Introduces the category codes in a composed message.</summary>
    public const string CategoriesPrefix = " Problem categories: ";

    /// <summary>Separates two category codes in a composed message.</summary>
    public const string CategorySeparator = ", ";

    /// <summary>Ends the list of category codes in a composed message.</summary>
    public const string CategoriesSuffix = ".";

    private readonly Dictionary<string, JobErrorCode> _byCode = new(StringComparer.Ordinal);

    private readonly Dictionary<string, JobCompositeErrorCode> _composites = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JobErrorCode> ByCode => _byCode;

    public IReadOnlyDictionary<string, JobCompositeErrorCode> Composites => _composites;

    public void Add(string code, string message)
    {
        CheckNewCode(code, message);
        _byCode.Add(code, new JobErrorCode(code, message));
    }

    public void AddComposite(string code, string message, IReadOnlyList<string> categoryCodes)
    {
        CheckNewCode(code, message);
        if (categoryCodes is null || categoryCodes.Count == 0)
        {
            throw new ArgumentException(
                $"Composite job error code '{code}' must list at least one category code.",
                nameof(categoryCodes)
            );
        }

        HashSet<string> distinct = new(StringComparer.Ordinal);
        foreach (string category in categoryCodes)
        {
            if (category is not null && _composites.ContainsKey(category))
            {
                throw new InvalidOperationException(
                    $"Category '{category}' of composite job error code '{code}' is itself a composite code."
                );
            }

            if (category is null || !IsSingleCode(category))
            {
                throw new InvalidOperationException(
                    $"Category '{category}' of composite job error code '{code}' is not a registered error code; "
                        + "register each category before the composite."
                );
            }

            if (!distinct.Add(category))
            {
                throw new InvalidOperationException(
                    $"Category '{category}' of composite job error code '{code}' is listed more than once."
                );
            }
        }

        int worstCase = WorstCaseLength(message, categoryCodes);
        if (worstCase > MaxMessageLength)
        {
            throw new InvalidOperationException(
                $"Composite job error code '{code}' could compose a message of {worstCase} characters with all of "
                    + $"its categories listed; the limit is {MaxMessageLength}."
            );
        }

        _composites.Add(code, new JobCompositeErrorCode(code, message, [.. categoryCodes]));
    }

    /// <summary>
    /// The length of the composite's message followed by every category code: the longest text a composition must
    /// keep, since category prose is added only while it fits.
    /// </summary>
    internal static int WorstCaseLength(string message, IReadOnlyList<string> categoryCodes) =>
        message.Length
        + CategoriesPrefix.Length
        + categoryCodes.Sum(category => category.Length)
        + (CategorySeparator.Length * (categoryCodes.Count - 1))
        + CategoriesSuffix.Length;

    private void CheckNewCode(string code, string message)
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

        if (_byCode.ContainsKey(code) || _composites.ContainsKey(code))
        {
            throw new InvalidOperationException($"Job error code '{code}' is registered more than once.");
        }
    }

    private bool IsSingleCode(string code) =>
        _byCode.ContainsKey(code)
        || JobErrorCode.Infrastructure.Any(infrastructure => infrastructure.Code == code);

    // \z rather than $: $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>A registered composite code, its fixed message, and the category codes a failure may list.</summary>
internal sealed class JobCompositeErrorCode(string code, string message, IReadOnlyList<string> categoryCodes)
{
    public string Code { get; } = code;

    public string Message { get; } = message;

    public IReadOnlyList<string> CategoryCodes { get; } = categoryCodes;

    public IReadOnlySet<string> AllowedCategories { get; } =
        new HashSet<string>(categoryCodes, StringComparer.Ordinal);
}

/// <summary>
/// The infrastructure codes plus the consumer codes registered at startup (spec D-7), and the composite codes whose
/// messages are composed from registered text only.
/// </summary>
internal sealed class JobErrorCodeRegistry(JobErrorCodeRegistrations registrations) : IJobErrorCodeRegistry
{
    private readonly Dictionary<string, JobErrorCode> _codes = new(
        JobErrorCode
            .Infrastructure.Concat(registrations.ByCode.Values)
            .Select(errorCode => KeyValuePair.Create(errorCode.Code, errorCode)),
        StringComparer.Ordinal
    );

    private readonly Dictionary<string, JobCompositeErrorCode> _composites = new(
        registrations.Composites,
        StringComparer.Ordinal
    );

    public bool TryGet(string code, [NotNullWhen(true)] out JobErrorCode? errorCode) =>
        _codes.TryGetValue(code, out errorCode);

    public bool TryCompose(
        string primaryCode,
        IReadOnlyList<string> categoryCodes,
        [NotNullWhen(true)] out string? message
    )
    {
        message = null;
        if (
            primaryCode is null
            || categoryCodes is null
            || categoryCodes.Count == 0
            || !_composites.TryGetValue(primaryCode, out JobCompositeErrorCode? composite)
        )
        {
            return false;
        }

        List<string> categories = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string category in categoryCodes)
        {
            if (category is null || !composite.AllowedCategories.Contains(category))
            {
                return false;
            }

            if (seen.Add(category))
            {
                categories.Add(category);
            }
        }

        // Every category code is listed: registration proved the full list fits. Category prose follows, in the
        // same order, only while the whole text stays within the limit; the first that does not fit ends it.
        StringBuilder text = new StringBuilder(composite.Message)
            .Append(JobErrorCodeRegistrations.CategoriesPrefix)
            .AppendJoin(JobErrorCodeRegistrations.CategorySeparator, categories)
            .Append(JobErrorCodeRegistrations.CategoriesSuffix);
        foreach (string category in categories)
        {
            string prose = _codes[category].Message;
            if (text.Length + 1 + prose.Length > JobErrorCodeRegistrations.MaxMessageLength)
            {
                break;
            }

            text.Append(' ').Append(prose);
        }

        message = text.ToString();
        return true;
    }
}
