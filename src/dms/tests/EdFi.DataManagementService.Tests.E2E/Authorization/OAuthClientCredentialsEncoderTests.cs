// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

[TestFixture]
public class Given_OAuth_Client_Credentials_Encoder
{
    [Test]
    public void It_percent_encodes_reserved_characters_before_creating_basic_credentials()
    {
        string basicParameter = OAuthClientCredentialsEncoder.CreateBasicSchemeParameter(
            "client:with+reserved",
            "secret:with%reserved+chars"
        );

        string decodedCredentials = Encoding.UTF8.GetString(Convert.FromBase64String(basicParameter));

        decodedCredentials.Should().Be(
            "client%3Awith%2Breserved:secret%3Awith%25reserved%2Bchars"
        );
    }
}
