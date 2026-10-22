# SPDX-License-Identifier: Apache-2.0
"""Read-only review aid; never invokes the converter or rewrites the oracle.

Compare policy facts independently extracted from database rows, XML, and JSON.
Run with Python 3 from any directory. This audits fixtures, not converter output.
"""

import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path

DIRECTORY = Path(__file__).resolve().parent
LEGACY = "http://ed-fi.org/ods/identity/claims/"
CURRENT = "http://ed-fi.org/identity/claims/"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def uri(value):
    if not value.startswith(LEGACY):
        return value
    suffix = value[len(LEGACY) :]
    return CURRENT + ("ed-fi/" if suffix and "/" not in suffix else "") + suffix


def name(value):
    return value.replace(" ", "").replace("-", "")


def policy():
    return {"claims": {}, "defaults": {}, "grants": {}}


def add(target, key, value):
    require(key not in target, f"Duplicate policy key: {key}")
    target[key] = value


def database_policy(source):
    result = policy()
    claims = {r["ResourceClaimId"]: uri(r["ClaimName"]) for r in source["claims"]}
    sets = {r["ClaimSetId"]: name(r["ClaimSetName"]) for r in source["claimSets"]}
    actions = {r["ActionId"]: r["ActionName"] for r in source["actions"]}
    strategies = {
        r["AuthorizationStrategyId"]: r["AuthorizationStrategyName"]
        for r in source["strategies"]
    }
    for row in source["claims"]:
        add(
            result["claims"],
            claims[row["ResourceClaimId"]],
            claims.get(row["ParentResourceClaimId"]),
        )
    for row in source["defaults"]:
        require(
            not row["ValidationRuleSetName"], "Unsupported populated validation rule"
        )
        selected = tuple(
            sorted(
                strategies[r["AuthorizationStrategyId"]]
                for r in source["defaultStrategies"]
                if r["ResourceClaimActionId"] == row["ResourceClaimActionId"]
            )
        )
        add(
            result["defaults"],
            (claims[row["ResourceClaimId"]], actions[row["ActionId"]]),
            selected,
        )
    for row in source["grants"]:
        require(
            not row["ValidationRuleSetNameOverride"],
            "Unsupported populated validation override",
        )
        selected = tuple(
            sorted(
                strategies[r["AuthorizationStrategyId"]]
                for r in source["overrides"]
                if r["ClaimSetResourceClaimActionId"]
                == row["ClaimSetResourceClaimActionId"]
            )
        )
        add(
            result["grants"],
            (
                claims[row["ResourceClaimId"]],
                sets[row["ClaimSetId"]],
                actions[row["ActionId"]],
            ),
            selected or None,
        )
    return result


def review():
    source = json.loads((DIRECTORY / "database-policy.json").read_text())
    expected = json.loads((DIRECTORY / "expected-claims.json").read_text())
    root = ET.parse(DIRECTORY / "representative.xml").getroot()
    provenance = json.loads((DIRECTORY / "provenance.json").read_text())
    for artifact, digest in provenance["artifacts"].items():
        require(
            hashlib.sha256((DIRECTORY / artifact).read_bytes()).hexdigest() == digest,
            f"Recorded artifact hash differs: {artifact}",
        )
    require(
        provenance["review"]["expectedClaimsSha256"]
        == provenance["artifacts"]["expected-claims.json"],
        "Reviewed expected-document hash differs",
    )
    xml_policy, json_policy = policy(), policy()
    catalog = []

    def visit(xml_claims, json_claims, parent=None):
        require(
            len(xml_claims) == len(json_claims), f"Child count differs under {parent}"
        )
        for xml_claim, json_claim in zip(xml_claims, json_claims):
            claim = uri(xml_claim.attrib["name"])
            require(claim == json_claim["name"], f"Claim order/name differs: {claim}")
            add(xml_policy["claims"], claim, parent)
            add(json_policy["claims"], json_claim["name"], parent)
            xml_defaults = xml_claim.findall("DefaultAuthorization/Action")
            json_defaults = json_claim.get("defaultAuthorization", {}).get(
                "actions", []
            )
            require(
                [a.attrib["name"] for a in xml_defaults]
                == [a["name"] for a in json_defaults],
                f"Default action order differs: {claim}",
            )
            require(
                bool(xml_defaults) == ("defaultAuthorization" in json_claim),
                f"Default presence differs: {claim}",
            )
            for action in xml_defaults:
                require(
                    not action.get("validationRuleSet"),
                    f"Unsupported validation rule: {claim}",
                )
                add(
                    xml_policy["defaults"],
                    (claim, action.attrib["name"]),
                    tuple(
                        s.attrib["name"]
                        for s in action.findall(
                            "AuthorizationStrategies/AuthorizationStrategy"
                        )
                    ),
                )
            for action in json_defaults:
                add(
                    json_policy["defaults"],
                    (claim, action["name"]),
                    tuple(s["name"] for s in action["authorizationStrategies"]),
                )
            xml_sets = xml_claim.findall("ClaimSets/ClaimSet")
            json_sets = json_claim.get("claimSets", [])
            require(
                [name(s.attrib["name"]) for s in xml_sets]
                == [s["name"] for s in json_sets],
                f"Claim-set order/names differ: {claim}",
            )
            require(
                (xml_claim.find("ClaimSets") is not None)
                == ("claimSets" in json_claim),
                f"Claim-set presence differs: {claim}",
            )
            for xml_set, json_set in zip(xml_sets, json_sets):
                set_name = name(xml_set.attrib["name"])
                if set_name not in catalog:
                    catalog.append(set_name)
                xml_actions = xml_set.findall("Actions/Action")
                require(
                    [a.attrib["name"] for a in xml_actions]
                    == [a["name"] for a in json_set["actions"]],
                    f"Grant order differs: {claim}, {set_name}",
                )
                for action in xml_actions:
                    require(
                        not action.get("validationRuleSetOverride"),
                        f"Unsupported validation override: {claim}",
                    )
                    overrides = action.find("AuthorizationStrategyOverrides")
                    add(
                        xml_policy["grants"],
                        (claim, set_name, action.attrib["name"]),
                        None
                        if overrides is None
                        else tuple(s.attrib["name"] for s in overrides),
                    )
                for action in json_set["actions"]:
                    add(
                        json_policy["grants"],
                        (claim, json_set["name"], action["name"]),
                        tuple(
                            s["name"] for s in action["authorizationStrategyOverrides"]
                        )
                        if "authorizationStrategyOverrides" in action
                        else None,
                    )
            require(
                (xml_claim.find("Claims") is not None) == ("claims" in json_claim),
                f"Children presence differs: {claim}",
            )
            visit(
                xml_claim.findall("Claims/Claim"), json_claim.get("claims", []), claim
            )

    require(root.tag == "SecurityMetadata", "Unexpected XML root")
    visit(root.findall("Claims/Claim"), expected["claimsHierarchy"])
    require(
        expected["claimSets"]
        == [{"claimSetName": n, "isSystemReserved": False} for n in catalog],
        "Catalog differs from first-reference order or imported flags",
    )
    database = database_policy(source)
    for section in database:
        require(
            database[section] == xml_policy[section], f"Database/XML {section} differ"
        )
        require(
            database[section] == json_policy[section], f"Database/JSON {section} differ"
        )
    # Compare every affected resource with the existing CMS standard, independently
    # of the converter and the mapping used by the three-way policy audit above.
    standard_path = (
        DIRECTORY.parents[3]
        / "backend/EdFi.DmsConfigurationService.Backend/Claims/Standards/ds52/Claims.json"
    )
    standard = json.loads(standard_path.read_text())

    def claim_names(claims):
        for claim in claims:
            yield claim["name"]
            yield from claim_names(claim.get("claims", []))

    standard_names = set(claim_names(standard["claimsHierarchy"]))
    core_resources = [
        row["ClaimName"].removeprefix(LEGACY)
        for row in source["claims"]
        if row["ClaimName"].startswith(LEGACY)
        and row["ClaimName"].removeprefix(LEGACY)
        and "/" not in row["ClaimName"].removeprefix(LEGACY)
    ]
    require(len(core_resources) == 342, "Expected 342 legacy core resources")
    for resource in core_resources:
        expected_uri = f"http://ed-fi.org/identity/claims/ed-fi/{resource}"
        require(
            expected_uri in standard_names,
            f"Core resource absent from ds52: {expected_uri}",
        )
        require(
            expected_uri in json_policy["claims"],
            f"Core resource not qualified: {expected_uri}",
        )
    external = {
        row["ClaimName"]
        for row in source["claims"]
        if not row["ClaimName"].startswith(LEGACY)
    }
    require(len(external) == 6, "Expected six external IMS claims")
    require(external <= json_policy["claims"].keys(), "External claims changed")
    print(
        "Verified 342 project-qualified core resources against ds52 Claims.json "
        "and six unchanged external IMS URIs."
    )
    print(
        f"Verified {len(database['claims'])} claims, {len(catalog)} referenced sets, {len(database['defaults'])} defaults, {len(database['grants'])} grants; all strategy lists, overrides, parents, ordering, and imported flags match."
    )


if __name__ == "__main__":
    review()
