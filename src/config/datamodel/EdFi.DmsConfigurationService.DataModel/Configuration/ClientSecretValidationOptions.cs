// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

[assembly: InternalsVisibleTo("EdFi.DmsConfigurationService.Backend.Tests.Unit")]

namespace EdFi.DmsConfigurationService.DataModel.Configuration;

public class ClientSecretValidationOptions
{
    public const string SectionName = "IdentitySettings:ClientSecretValidation";
    public const int MaximumAllowedLength = 1024;

    public int MinimumLength { get; set; } = 32;
    public int MaximumLength { get; set; } = 128;
}

public class ClientSecretValidationOptionsValidator : IValidateOptions<ClientSecretValidationOptions>
{
    public ValidateOptionsResult Validate(string? name, ClientSecretValidationOptions options)
    {
        if (options.MinimumLength < 4)
        {
            return ValidateOptionsResult.Fail(
                "Invalid ClientSecretValidation configuration: MinimumLength must be greater than or equal to 4."
            );
        }

        if (options.MaximumLength < options.MinimumLength)
        {
            return ValidateOptionsResult.Fail(
                "Invalid ClientSecretValidation configuration: MaximumLength must be greater than or equal to MinimumLength."
            );
        }

        if (options.MaximumLength > ClientSecretValidationOptions.MaximumAllowedLength)
        {
            return ValidateOptionsResult.Fail(
                $"Invalid ClientSecretValidation configuration: MaximumLength must not exceed {ClientSecretValidationOptions.MaximumAllowedLength}."
            );
        }

        return ValidateOptionsResult.Success;
    }
}

public static class ClientSecretValidation
{
    private const string LowercaseAlphabet = "abcdefghijklmnopqrstuvwxyz";
    private const string UppercaseAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitAlphabet = "0123456789";

    /// <summary>
    /// The set of special characters allowed in client secrets.
    /// Excludes characters that may cause issues in shell scripts, URLs, or JSON: ~ ` | < > " '
    /// </summary>
    private const string SpecialAlphabet = "!@#$%^&*()-_=+[]{}:;,.?";
    private static readonly string EscapedSpecialCharacterClass = BuildRegexCharacterClass(SpecialAlphabet);

    /// <summary>
    /// Characters that are unsafe for un-encoded credential transport (HTTP Basic / x-www-form-urlencoded).
    /// Trailing space ensures the exclusion set stays complete if SpecialAlphabet ever changes.
    /// </summary>
    internal const string TransportUnsafeSpecialCharacters = "+%=& ";

    /// <summary>
    /// The subset of SpecialAlphabet safe for generated secrets — excludes transport-unsafe characters.
    /// Derived from SpecialAlphabet so the generated set is always a subset of the validation set.
    /// Value today: "!@#$^*()-_[]{}:;,.?" (19 chars).
    /// </summary>
    internal static readonly string GeneratedSpecialAlphabet = new(
        SpecialAlphabet.Where(c => !TransportUnsafeSpecialCharacters.Contains(c)).ToArray()
    );

    internal static readonly string GeneratedClientSecretAlphabet =
        LowercaseAlphabet + UppercaseAlphabet + DigitAlphabet + GeneratedSpecialAlphabet;

    public static string BuildComplexityPattern(ClientSecretValidationOptions options) =>
        $@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[{EscapedSpecialCharacterClass}]).{{{options.MinimumLength},{options.MaximumLength}}}$";

    public static string BuildComplexityErrorMessage(ClientSecretValidationOptions options) =>
        $"Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be {options.MinimumLength} to {options.MaximumLength} characters long.";

    public static string BuildLengthErrorMessage(string settingPath, ClientSecretValidationOptions options) =>
        $"{settingPath} must be between {options.MinimumLength} and {options.MaximumLength} characters long.";

    public static bool IsWithinLengthRange(string value, ClientSecretValidationOptions options) =>
        value.Length >= options.MinimumLength && value.Length <= options.MaximumLength;

    /// <summary>
    /// Generates a cryptographically random client secret of at least <paramref name="options"/>.<see cref="ClientSecretValidationOptions.MinimumLength"/> characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CMS-generated secrets are Basic/form-safe: the generator draws special characters exclusively
    /// from <see cref="GeneratedSpecialAlphabet"/>, which excludes <c>+ % = &amp;</c> and space —
    /// the characters significant to HTTP Basic and <c>x-www-form-urlencoded</c> credential transport.
    /// This is why <see cref="GeneratedSpecialAlphabet"/> is narrower than <see cref="SpecialAlphabet"/>.
    /// </para>
    /// <para>
    /// Caller-supplied secrets are validated permissively via <see cref="SpecialAlphabet"/> /
    /// <see cref="BuildComplexityPattern"/> and MAY contain <c>+ % = &amp;</c>.
    /// A client using HTTP Basic that does not URL-encode its credentials (per RFC 6749 §2.3.1)
    /// must URL-encode any self-supplied secret that contains those characters before transmitting it.
    /// </para>
    /// </remarks>
    public static string GenerateSecretWithMinimumLength(ClientSecretValidationOptions options)
    {
        if (options.MaximumLength < options.MinimumLength)
        {
            throw new InvalidOperationException(
                "Client secret validation options are invalid. MaximumLength must be greater than or equal to MinimumLength."
            );
        }

        if (options.MinimumLength < 4)
        {
            throw new InvalidOperationException(
                "Client secret validation options are invalid. MinimumLength must be at least 4 to satisfy the client secret complexity requirements."
            );
        }

        if (options.MaximumLength > ClientSecretValidationOptions.MaximumAllowedLength)
        {
            throw new InvalidOperationException(
                $"Client secret validation options are invalid. MaximumLength must not exceed {ClientSecretValidationOptions.MaximumAllowedLength}."
            );
        }

        char[] secretCharacters =
        [
            GetRandomCharacter(LowercaseAlphabet),
            GetRandomCharacter(UppercaseAlphabet),
            GetRandomCharacter(DigitAlphabet),
            GetRandomCharacter(GeneratedSpecialAlphabet),
        ];

        if (options.MinimumLength > secretCharacters.Length)
        {
            Array.Resize(ref secretCharacters, options.MinimumLength);

            for (var index = 4; index < secretCharacters.Length; index++)
            {
                secretCharacters[index] = GetRandomCharacter(GeneratedClientSecretAlphabet);
            }
        }

        Shuffle(secretCharacters);
        return new string(secretCharacters);

        static char GetRandomCharacter(string alphabet) =>
            alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        static void Shuffle(char[] buffer)
        {
            for (var index = buffer.Length - 1; index > 0; index--)
            {
                var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
                (buffer[index], buffer[swapIndex]) = (buffer[swapIndex], buffer[index]);
            }
        }
    }

    private static string BuildRegexCharacterClass(string value)
    {
        StringBuilder builder = new(value.Length * 2);

        foreach (char character in value)
        {
            if (character is '\\' or '-' or ']' or '^' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
