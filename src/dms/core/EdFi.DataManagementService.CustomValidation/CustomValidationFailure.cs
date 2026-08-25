// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.CustomValidation;

/// <summary>
/// A single failure reported by a custom validator, matching one of the two buckets DMS's own 400
/// response body already carries: a failure tied to a JSON path within the document, or a failure
/// about the document as a whole.
/// This is a closed hierarchy with exactly two cases, <see cref="OnPath"/> and
/// <see cref="OnResource"/>: no third case is declarable in C# outside this assembly, so handling
/// both is handling all of them. The compiler does not know that, and a two-arm switch expression
/// still reports CS8509, so a consumer building with warnings as errors adds a discard arm.
/// A private constructor alone does not close a record hierarchy: the compiler synthesizes a
/// protected copy constructor on every unsealed record, and an external assembly can chain to it.
/// Closure is enforced instead by the abstract <c>private protected</c> member below, which only a
/// case declared in this assembly can satisfy.
/// </summary>
public abstract record CustomValidationFailure
{
    private CustomValidationFailure() { }

    /// <summary>
    /// Closes the hierarchy against any usable case declared outside this assembly. An external
    /// record can still reach the synthesized protected copy constructor, and an external
    /// <c>abstract</c> record that does so compiles, but no external type can override a
    /// <c>private protected</c> abstract member, so the first concrete type in any such chain fails
    /// with CS0534.
    /// This holds for code compiled against this assembly. It is contingent on this assembly
    /// granting no <c>InternalsVisibleTo</c>: a friend assembly can override the member and declare
    /// a concrete case, which is why a test asserts no such grant exists.
    /// The member carries no behavior and is never invoked.
    /// </summary>
    private protected abstract void EnsureClosed();

    /// <summary>
    /// A failure tied to a specific JSON path within the document.
    /// Produces a <c>validationErrors</c> entry, the same shape core schema validation uses for a
    /// path-level failure.
    /// A bare <c>"$."</c> is a valid, non-degenerate <see cref="JsonPath"/>: it is DMS's own
    /// document-level <c>validationErrors</c> key, so an implementer wanting parity with a core
    /// document-level failure uses <c>OnPath("$.", ...)</c> rather than <see cref="OnResource"/>.
    /// </summary>
    public sealed record OnPath : CustomValidationFailure
    {
        /// <summary>
        /// Constructs a path-level failure.
        /// </summary>
        /// <param name="jsonPath">
        /// The JSON path the failure applies to. Must be non-empty and "$."-prefixed; a bare "$" is
        /// rejected because it is not "$."-prefixed, while a bare "$." is accepted as DMS's own
        /// document-level path.
        /// </param>
        /// <param name="message">The failure message. Must be non-empty.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="jsonPath"/> or <paramref name="message"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="jsonPath"/> is empty, whitespace, or not "$."-prefixed, or
        /// when <paramref name="message"/> is empty or whitespace.
        /// </exception>
        public OnPath(string jsonPath, string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
            if (!jsonPath.StartsWith("$.", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{nameof(jsonPath)} must be \"$.\"-prefixed.", nameof(jsonPath));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            JsonPath = jsonPath;
            Message = message;
        }

        private protected override void EnsureClosed()
        {
            // Closure marker only; see CustomValidationFailure.EnsureClosed.
        }

        /// <summary>
        /// The "$."-prefixed JSON path the failure applies to.
        /// </summary>
        public string JsonPath { get; }

        /// <summary>
        /// The failure message.
        /// </summary>
        public string Message { get; }
    }

    /// <summary>
    /// A failure about the document as a whole, with no JSON path, such as a cross-field or
    /// external-lookup rejection.
    /// Produces an <c>errors</c> entry, the same shape <c>Middleware/ParseBodyMiddleware.cs</c> and
    /// <c>Middleware/ValidateMatchingDocumentUuidsMiddleware.cs</c> already emit.
    /// </summary>
    public sealed record OnResource : CustomValidationFailure
    {
        /// <summary>
        /// Constructs a resource-level failure.
        /// </summary>
        /// <param name="message">The failure message. Must be non-empty.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="message"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="message"/> is empty or whitespace.
        /// </exception>
        public OnResource(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            Message = message;
        }

        private protected override void EnsureClosed()
        {
            // Closure marker only; see CustomValidationFailure.EnsureClosed.
        }

        /// <summary>
        /// The failure message.
        /// </summary>
        public string Message { get; }
    }
}
