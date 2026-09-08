// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Middleware;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Holds the full Authorization header classification matrix for the shared
/// <see cref="AuthorizationHeaderParser"/>. The JWT authentication and JWT
/// role-authentication middleware test suites each keep only a representative
/// wiring case per error detail; classification cases belong here.
/// </summary>
[TestFixture]
[Parallelizable]
public class AuthorizationHeaderParserTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Malformed_Authorization_Header : AuthorizationHeaderParserTests
    {
        // Blank values: a present-but-blank header has no parseable scheme.
        [TestCase("")]
        [TestCase("   ")]
        // Repeated-header folds: a comma in the scheme is the fold signature of a repeated
        // Authorization header (unparseable per RFC 7235), including a blank first value.
        [TestCase(",")]
        [TestCase(",Bearer abc")]
        [TestCase("Bearer,junk")]
        // Comma in the token: not in the RFC 6750 token alphabet; also the fold signature
        // when the first value is a well-formed Bearer credential.
        [TestCase("Bearer valid,junk")]
        [TestCase("Bearer valid,")]
        // Whitespace in or before the token: a JWT never contains whitespace, and only
        // space(s) are a valid scheme/token separator.
        [TestCase("Bearer abc def")]
        [TestCase("Bearer\tabc")]
        [TestCase("Bearer ab\tc")]
        [TestCase("Bearer \tabc")]
        public void It_classifies_the_header_as_invalid(string authHeader)
        {
            AuthorizationHeaderResult result = AuthorizationHeaderParser.Parse(authHeader);

            result.IsValid.Should().BeFalse();
            result.ErrorDetail.Should().Be("Invalid Authorization header.");
            result.Token.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Bearer_Header_Without_A_Token_Value : AuthorizationHeaderParserTests
    {
        [TestCase("Bearer")]
        [TestCase("Bearer ")]
        [TestCase("Bearer   ")]
        [TestCase("bearer")]
        public void It_classifies_the_token_value_as_missing(string authHeader)
        {
            AuthorizationHeaderResult result = AuthorizationHeaderParser.Parse(authHeader);

            result.IsValid.Should().BeFalse();
            result.ErrorDetail.Should().Be("Missing Authorization header bearer token value.");
            result.Token.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Non_Bearer_Authorization_Header : AuthorizationHeaderParserTests
    {
        [TestCase("Basic dXNlcjpwYXNz")]
        // No scheme/token separator: guards against Bearer prefix-match regressions.
        [TestCase("BearerToken")]
        // Commas in a single well-formed non-Bearer credential's auth-params are legitimate
        // (RFC 7235); the scheme is unknown, the header is not malformed.
        [TestCase("Digest username=\"u\", realm=\"r\", nonce=\"n\"")]
        [TestCase("AWS4-HMAC-SHA256 Credential=abc, SignedHeaders=host, Signature=def")]
        public void It_classifies_the_scheme_as_unknown(string authHeader)
        {
            AuthorizationHeaderResult result = AuthorizationHeaderParser.Parse(authHeader);

            result.IsValid.Should().BeFalse();
            result.ErrorDetail.Should().Be("Unknown Authorization header scheme.");
            result.Token.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Well_Formed_Bearer_Header : AuthorizationHeaderParserTests
    {
        [TestCase("Bearer abc", "abc")]
        // The scheme is case-insensitive.
        [TestCase("bearer abc", "abc")]
        // Multiple separating spaces are stripped; only the token remains.
        [TestCase("Bearer   abc", "abc")]
        // Surrounding optional whitespace is trimmed before parsing.
        [TestCase(" Bearer abc ", "abc")]
        public void It_extracts_the_bearer_token(string authHeader, string expectedToken)
        {
            AuthorizationHeaderResult result = AuthorizationHeaderParser.Parse(authHeader);

            result.IsValid.Should().BeTrue();
            result.Token.Should().Be(expectedToken);
            result.ErrorDetail.Should().BeNull();
        }
    }
}
