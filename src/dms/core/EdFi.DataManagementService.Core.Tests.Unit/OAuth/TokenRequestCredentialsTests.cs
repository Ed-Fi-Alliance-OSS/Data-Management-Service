// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.OAuth;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

public class TokenRequestCredentialsTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Credentials_Rendered_As_A_String
    {
        private string _rendered = default!;

        [SetUp]
        public void Setup()
        {
            _rendered = new TokenRequestCredentials("my-client-id", "my-client-secret").ToString();
        }

        [Test]
        public void It_does_not_render_the_secret()
        {
            _rendered.Should().NotContain("my-client-secret");
        }

        [Test]
        public void It_does_not_render_the_client_id()
        {
            _rendered.Should().NotContain("my-client-id");
        }
    }

    /// <summary>
    /// An empty or whitespace-only field is no second mechanism; one non-blank field is.
    /// </summary>
    [TestFixture(null, null, false)]
    [TestFixture("", null, false)]
    [TestFixture(null, "", false)]
    [TestFixture("", "", false)]
    [TestFixture("   ", "\t", false)]
    [TestFixture("my-client", null, true)]
    [TestFixture("", "my-secret", true)]
    [TestFixture("my-client", "my-secret", true)]
    [Parallelizable]
    public class Given_Body_Fields(string? clientId, string? clientSecret, bool expectedNonBlank)
    {
        [Test]
        public void It_reports_whether_either_field_is_non_blank()
        {
            new TokenRequestCredentials(clientId, clientSecret)
                .HasNonBlankField.Should()
                .Be(expectedNonBlank);
        }
    }

    /// <summary>
    /// RFC 6749 section 3.2: a parameter sent without a value is treated as omitted.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Empty_Body_Fields
    {
        private readonly TokenRequestCredentials _credentials = new(string.Empty, string.Empty);

        [Test]
        public void It_holds_them_as_absent()
        {
            _credentials.ClientId.Should().BeNull();
            _credentials.ClientSecret.Should().BeNull();
        }

        [Test]
        public void It_equals_a_body_with_no_fields()
        {
            _credentials.Should().Be(TokenRequestCredentials.None);
        }
    }

    /// <summary>
    /// A whitespace-only value is a value, and is kept as sent.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_Whitespace_Only_Body_Fields
    {
        [Test]
        public void It_keeps_the_values()
        {
            var credentials = new TokenRequestCredentials(" ", "\t");
            credentials.ClientId.Should().Be(" ");
            credentials.ClientSecret.Should().Be("\t");
        }
    }
}
