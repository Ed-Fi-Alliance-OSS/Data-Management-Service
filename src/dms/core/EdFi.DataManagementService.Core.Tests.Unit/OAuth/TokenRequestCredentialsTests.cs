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
    }

    [TestFixture(null, null, false)]
    [TestFixture("", null, true)]
    [TestFixture(null, "", true)]
    [TestFixture("my-client", "my-secret", true)]
    [Parallelizable]
    public class Given_Body_Fields(string? clientId, string? clientSecret, bool expectedPresent)
    {
        [Test]
        public void It_reports_presence_whatever_the_values()
        {
            new TokenRequestCredentials(clientId, clientSecret).IsPresent.Should().Be(expectedPresent);
        }
    }
}
