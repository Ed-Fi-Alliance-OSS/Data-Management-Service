// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.DataModel.Configuration;

public class ClientSecretValidationOptions
{
    public const string SectionName = "IdentitySettings:ClientSecretValidation";

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

        return ValidateOptionsResult.Success;
    }
}

public static class ClientSecretValidation
{
    private const string LowercaseAlphabet = "abcdefghijklmnopqrstuvwxyz";
    private const string UppercaseAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitAlphabet = "0123456789";
    private const string SpecialAlphabet = "!@#$%^&*()-_=+[]{}:;,.?";
    private static readonly string EscapedSpecialCharacterClass =
        BuildRegexCharacterClass(SpecialAlphabet);
    private const string GeneratedClientSecretAlphabet =
        LowercaseAlphabet + UppercaseAlphabet + DigitAlphabet + SpecialAlphabet;

    public static string BuildComplexityPattern(ClientSecretValidationOptions options)
        => $@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[{EscapedSpecialCharacterClass}]).{{{options.MinimumLength},{options.MaximumLength}}}$";

    public static string BuildComplexityErrorMessage(ClientSecretValidationOptions options)
        => $"Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be {options.MinimumLength} to {options.MaximumLength} characters long.";

    public static string BuildLengthErrorMessage(
        string settingPath,
        ClientSecretValidationOptions options
    ) => $"{settingPath} must be between {options.MinimumLength} and {options.MaximumLength} characters long.";

    public static bool IsWithinLengthRange(
        string value,
        ClientSecretValidationOptions options
    ) => value.Length >= options.MinimumLength && value.Length <= options.MaximumLength;

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

        char[] secretCharacters =
        [
            GetRandomCharacter(LowercaseAlphabet),
            GetRandomCharacter(UppercaseAlphabet),
            GetRandomCharacter(DigitAlphabet),
            GetRandomCharacter(SpecialAlphabet),
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
