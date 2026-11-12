# Hybrid policy oracle

The launcher mounts only `fragments/`, byte-for-byte, and disables unrestricted claims
management. There are no standard E2E harness fragments. Both Data Standards use these
same four files, with ordinally unambiguous numeric prefixes:

- `00-parent-claims.json` introduces a parent with Read/NamespaceBased and
  Create/NoFurtherAuthorizationRequired defaults, grants HybridPolicy both actions,
  and adds two name-only children. Its student Read grant uses the bundled people
  default, RelationshipsWithEdOrgsAndPeople. The non-parent entry registers HybridPolicy.
- `10-winner-claims.json` supplies the overridden child's Read/NoFurtherAuthorizationRequired.
  Create is still inherited from the parent.
- `20-loser-claims.json` requests a conflicting Read/NamespaceBased plus Delete grant
  for the same set and child. Existing first-association precedence discards this
  entire later association: Read stays NoFurtherAuthorizationRequired and Delete is absent.
- `30-empty-claims.json` registers HybridEmpty through a matching student entry with
  an empty override list. It grants nothing; both claims and authorizations are empty.

`expected-catalog.json` lists all fourteen bundled sets plus those two fixture sets.
`expected-metadata-5.2.json` and `expected-metadata-6.1.json` are explicit permission
oracles authored from the policies above, not captured resolver output. They contain
all HybridPolicy/HybridEmpty permissions and one selected EdFiSandbox baseline claim.
The selected accreditationStatusDescriptor is under `tpdm` for 5.2 and `ed-fi` for 6.1;
the other version's URI must be absent. Both inherit the bundled systemDescriptors
CRUD + ReadChanges grants: Create/Update/Delete use NamespaceBased; Read/ReadChanges
use NoFurtherAuthorizationRequired. IDs in these fixtures are only local references.

The service test dereferences metadata independently, compares each custom set exactly,
and checks the version-specific baseline permission and full catalog against these
literals. It also compares every API permission to offline merge/resolve output,
including strategy sequence order, then runs CLI verify against the full merged
input and requires exit 0 and the exact match report. The baseline sample is deliberately
bounded; full API/offline parity covers the remaining bundled permissions.

Run each case with fresh storage:

```sh
pwsh ./eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -Configuration Release -EnvironmentFile ./.env.config.e2e -SecurityMetadataToolsScenario Hybrid -DataStandardVersion 5.2
pwsh ./eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -Configuration Release -EnvironmentFile ./.env.config.e2e -SecurityMetadataToolsScenario Hybrid -DataStandardVersion 6.1
```

Each run retains API/offline JSON, merged input, command streams, allowlisted container
policy settings, source/mount SHA-256 hashes, and its exact named TRX result under
`TestResults/cms-isolation/<backend>/Hybrid/<version>/<run>/`. The bearer token is
acquired by the NUnit fixture and passed to verify only in the child environment;
no credentials or token responses are written as evidence. The source and mount hashes
must match before and after, and verify must leave the merged input bytes unchanged.
