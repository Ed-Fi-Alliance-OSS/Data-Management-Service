// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml.Linq;
using EdFi.DmsConfigurationService.SecurityMetadataTools.Model;
using Action = EdFi.DmsConfigurationService.SecurityMetadataTools.Model.Action;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

internal static class XmlToClaimsParser
{
    public static Claim[] ParseXml(string xmlFilePath)
    {
        XDocument doc = XDocument.Load(xmlFilePath);
        if (doc.Root?.Element("Claims") is not XElement claimsRoot)
        {
            return [];
        }

        return claimsRoot.Elements("Claim").Select(ParseClaim).ToArray();
    }

    private static Claim ParseClaim(XElement claimElement)
    {
        return new Claim
        {
            ClaimId = int.TryParse(claimElement.Attribute("claimId")?.Value, out var claimId) ? claimId : 0,
            Name = claimElement.Attribute("name")?.Value ?? string.Empty,
            DefaultAuthorization = claimElement.Element("DefaultAuthorization") is not null
                ? ParseDefaultAuthorization(claimElement.Element("DefaultAuthorization")!)
                : null,
            ClaimSets =
                claimElement.Element("ClaimSets")?.Elements("ClaimSet").Select(ParseClaimSet).ToList() ?? [],
            Claims = claimElement.Element("Claims")?.Elements("Claim").Select(ParseClaim).ToList(),
        };
    }

    private static DefaultAuthorization ParseDefaultAuthorization(XElement defaultAuthorizationElement)
    {
        return new DefaultAuthorization
        {
            Actions = ParseActions(defaultAuthorizationElement.Elements("Action")),
        };
    }

    private static List<Action> ParseActions(IEnumerable<XElement> actionElements)
    {
        return actionElements
            .Select(x => new Action
            {
                Name = x.Attribute("name")?.Value,
                AuthorizationStrategies = x.Element("AuthorizationStrategies")
                    ?.Elements("AuthorizationStrategy")
                    .Select(a => new AuthorizationStrategy { Name = a.Attribute("name")?.Value })
                    .ToList(),
            })
            .ToList();
    }

    private static ClaimSet ParseClaimSet(XElement claimSetElement)
    {
        return new ClaimSet
        {
            Name = claimSetElement
                .Attribute("name")
                ?.Value.Replace(" ", string.Empty)
                .Replace("-", string.Empty),
            Actions = claimSetElement
                .Element("Actions")
                ?.Elements("Action")
                .Select(ParseClaimSetAction)
                .ToList(),
        };
    }

    private static ClaimSetAction ParseClaimSetAction(XElement actionElement)
    {
        return new ClaimSetAction
        {
            Name = actionElement.Attribute("name")?.Value,
            AuthorizationStrategyOverrides = actionElement
                .Element("AuthorizationStrategyOverrides")
                ?.Elements("AuthorizationStrategy")
                .Select(a => new AuthorizationStrategy { Name = a.Attribute("name")?.Value })
                .ToList(),
        };
    }
}
