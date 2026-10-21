# ODS security metadata acceptance fixture — DMS-1617-01

## Review status

**DMS-1617-01 fixture review complete.** The checked-in expected document is the
acceptance oracle for the later conversion, package, and upload tasks. The
current fixture task requires independent preparation, policy review, and
reproduction evidence; it does not require personal approval from Brad. The
earlier pending-approval notice added a gate beyond that task contract and is
superseded by this technical review record. No personal approval is claimed.

| Review artifact | SHA-256 |
| --- | --- |
| [Expected complete claims document](expected-claims.json) | `8884f815c595e687d43e8fd1fdd20ef28ada665f90343febf42617bb578e6b43` |
| [Actual database export](representative.xml) | `2494968160141a1b88918d4a043fc8451e43fdb87e80e32fd0cf64160e447920` |
| [Independent database rows](database-policy.json) | `ffba5a1b4c6e260113bfa026c65889f262d1bb52642e905520bf81ea428cf5dc` |

Technical review: **Codex, 2026-10-21**, against the exact hashes above. Reviewed
the pinned exporter and independent capture query; rechecked both previously
captured fresh-database exports and row sets, all artifact/export hashes, the
complete database/XML/JSON policy audit, and the existing CMS JSON schema.
Any change to the expected document requires independent review of its new hash.
The full converter comparison is implemented separately in DMS-1617-04.

## Provenance and supported scope

Captured on 2026-10-21 from a fresh, isolated `EdFi_Security` SQL Server database.
This is the standard **ODS/API 7.3.2, Data Standard 5.2.0** security seed with no
extensions or optional feature scripts. No hand-authored policy was inserted.
The ODS API itself was not started; the standard database deployment tool applied
the published source scripts to a new database.

- [Ed-Fi-ODS v7.3.2](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/e489317ea77f245aff99d57374165c238848f9a0):
  `e489317ea77f245aff99d57374165c238848f9a0`. Structure scripts under
  `Artifacts/MsSql/Structure/Security`; seeds under
  `Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/MsSql/Data/Security`.
- [Ed-Fi-ODS-Implementation v7.3.2](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/tree/8b5ccf9b2f64dce214db0e364eada267822af4d7):
  `8b5ccf9b2f64dce214db0e364eada267822af4d7`. Included in the standard deployment
  search paths; all 36 executed security scripts came from Ed-Fi-ODS.
- [Pinned XML export script](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/blob/5bca6615e1ce06b84a87e8512cfbe69a1249dd9a/SecurityMetadata/Export/MsSql/Security-Metadata-to-XML.sql):
  `5bca6615e1ce06b84a87e8512cfbe69a1249dd9a`. Executed unmodified, including its
  `USE EdFi_Security`, function/view definitions, and final SELECT.
- Deployment tool: `EdFi.Suite3.Db.Deploy` **4.1.52**, command `EdFi.Db.Deploy`,
  reporting `1.0.0-4.1.52+52163274854a56022f8fdbee135932439639cab2`.
- SQL Server **2025 RTM-CU8-GDR, 17.0.4085.5**, Enterprise Developer Edition on
  Linux. Container image digest and each applied script's path/SHA-256 are in
  [provenance.json](provenance.json). The database has no single schema-version
  field; the 36 ordered `DeployJournal` entries identify its migration state.

Only this export format and provenance have been verified. These files do not
establish support for a range of ODS versions or runtime support for every
imported authorization strategy. Optional `Changes`/`RecordOwnership` scripts,
extensions, and integration-harness seeds were not selected.

## Independent expected document and review

The expected JSON was authored from the flat relational rows captured by
[capture-policy.sql](capture-policy.sql), independently of the production
converter and its output. The authoring pass followed parent IDs, joined action
and strategy IDs, and retained only locally declared defaults and grants. It
did not flatten inherited permissions. Claim and claim-set ID order matched the
observed export; action order follows action ID, and strategy order follows name.
The catalog follows first reference in the depth-first hierarchy traversal.

[verify-fixtures.py](verify-fixtures.py) is a read-only review aid. It independently
compares database, XML, and JSON policy facts for every claim, parent, default,
grant, strategy, and override. It also compares XML/JSON list order, optional
container presence, catalog references, and imported-set flags. It never runs
the converter or generates expected JSON. Tests must consume the checked-in
expected document unchanged.

Inventory verified against the database:

| Item | Count / treatment |
| --- | --- |
| Claims | 379, including 16 roots; all nesting retained |
| Claims without local defaults | 349; no inherited defaults copied into children |
| Referenced claim sets | 12; all `isSystemReserved: false` |
| Local default actions | 116, containing 123 strategy assignments |
| Claim-set actions | 266, containing 26 strategy override assignments |
| Legacy claim URIs migrated | 373 |
| Unchanged external claim URIs | 6 OneRoster URIs under `https://purl.imsglobal.org/spec/or/v1p2/scope` |
| Populated validation-rule attributes | 0 |

Only the leading `http://ed-fi.org/ods/identity/claims/` becomes
`http://ed-fi.org/identity/claims/`. Only literal spaces and hyphens are removed
from source set names. The complete source-to-catalog mapping is:

| Source name | Catalog/reference name |
| --- | --- |
| SIS Vendor | SISVendor |
| Ed-Fi Sandbox | EdFiSandbox |
| Roster Vendor | RosterVendor |
| Assessment Vendor | AssessmentVendor |
| Assessment Read | AssessmentRead |
| Bootstrap Descriptors and EdOrgs | BootstrapDescriptorsandEdOrgs |
| District Hosted SIS Vendor | DistrictHostedSISVendor |
| AB Connect | ABConnect |
| Ed-Fi API Publisher - Reader | EdFiAPIPublisherReader |
| Ed-Fi API Publisher - Writer | EdFiAPIPublisherWriter |
| Finance Vendor | FinanceVendor |
| Education Preparation Program | EducationPreparationProgram |

All ten used strategy names survive unchanged: `NamespaceBased`,
`NoFurtherAuthorizationRequired`, `RelationshipsWithEdOrgsAndPeople`,
`RelationshipsWithEdOrgsAndPeopleIncludingDeletes`,
`RelationshipsWithEdOrgsAndPeopleInverted`, `RelationshipsWithEdOrgsOnly`,
`RelationshipsWithStudentsOnly`, `RelationshipsWithStudentsOnlyIncludingDeletes`,
`RelationshipsWithStudentsOnlyThroughResponsibility`, and
`RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes`.
The source strategy table contains 13 definitions; unused definitions are not
exported by the pinned script and are not policy assignments.

The expected JSON parses and passes the repository's existing CMS
`JsonSchemaForClaims.json` under a Draft 2020-12 JSON Schema validator. Actual
converter-to-CMS-validator integration remains DMS-1617-05.

Verification on 2026-10-21: a second fresh database deployment reproduced both
the formatted XML bytes and independent source rows exactly; all three artifact
hashes matched. The documented Db.Deploy 4.1.52 installation succeeded in a fresh
tool directory. The SecurityMetadataTools Release unit suite passed 28/28 tests
with no skips, and the XML, JSON, and database evidence copied unchanged into
test output. These existing tests exercise synthetic cases; the representative
converter comparison remains DMS-1617-04. Final fixture-review verification
rechecked the retained outputs from both database deployments without rebuilding
the databases. Local TRX: `TestResults/DMS-1617-01-final.trx` relative to the test
project directory.

## Reproduce the database and export

Prerequisites: Git, Docker Engine, .NET 8 runtime for Db.Deploy 4.1.52, .NET SDK
for tool installation, and Python 3. Run in a fresh scratch directory. Replace
`fixture` below with the absolute path to this directory in the DMS checkout.
The password is a disposable local fixture password. Use an unused container
name and loopback port; this procedure operates only on its isolated container.

```bash
fixture=/absolute/path/to/Fixtures/OdsSecurityMetadata
git clone https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS.git
git -C Ed-Fi-ODS checkout --detach e489317ea77f245aff99d57374165c238848f9a0
git clone https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation.git
git -C Ed-Fi-ODS-Implementation checkout --detach 8b5ccf9b2f64dce214db0e364eada267822af4d7
git -C Ed-Fi-ODS-Implementation show \
  5bca6615e1ce06b84a87e8512cfbe69a1249dd9a:SecurityMetadata/Export/MsSql/Security-Metadata-to-XML.sql \
  > export.sql

dotnet tool install EdFi.Suite3.Db.Deploy --version 4.1.52 --tool-path ./tools \
  --add-source https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json

docker run --rm -d --name dms-1617-ods-fixture \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Dms1617_FixtureOnly!' \
  -p 127.0.0.1:14347:1433 \
  mcr.microsoft.com/mssql/server@sha256:b036b61e953e6e660f04514fc3f703b995a9cdda569cf96d07d3f751240f615a

# Proceed once this query succeeds; inspect docker logs if SQL Server is not ready.
docker exec dms-1617-ods-fixture /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Dms1617_FixtureOnly!' -C -b -Q 'SELECT @@VERSION'

./tools/EdFi.Db.Deploy deploy --database Security --engine SqlServer \
  --standardVersion 5.2.0 \
  --filePaths "$PWD/Ed-Fi-ODS,$PWD/Ed-Fi-ODS/Application/EdFi.Ods.Standard,$PWD/Ed-Fi-ODS-Implementation" \
  --connectionString 'Server=127.0.0.1,14347;Database=EdFi_Security;User Id=sa;Password=Dms1617_FixtureOnly!;TrustServerCertificate=True' \
  > seed.log 2>&1
# Require exit 0 and Upgrade successful; preserve seed.log for diagnosis.

docker cp export.sql dms-1617-ods-fixture:/tmp/export.sql
docker exec dms-1617-ods-fixture /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Dms1617_FixtureOnly!' -C -b -y 0 \
  -i /tmp/export.sql > export.raw.txt
docker cp "$fixture/capture-policy.sql" dms-1617-ods-fixture:/tmp/capture.sql
docker exec dms-1617-ods-fixture /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Dms1617_FixtureOnly!' -C -b -d EdFi_Security -y 0 \
  -i /tmp/capture.sql > policy.raw.json
```

`-y 0` avoids sqlcmd's default truncation. The checked-in XML differs from the
SQL result only by indentation, self-closing-tag spacing, and the final newline;
sqlcmd's database-context/row-count messages are excluded. No policy edits,
filtering, renaming, or sorting were made to the XML. To format fresh artifacts
outside the fixture directory and compare them with the checked-in sources:

```bash
python3 - "$fixture" <<'PY'
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

fixture = Path(sys.argv[1])
raw = Path('export.raw.txt').read_text()
start = raw.index('<SecurityMetadata>')
end = raw.index('</SecurityMetadata>') + len('</SecurityMetadata>')
root = ET.fromstring(raw[start:end])
ET.indent(root, space='  ')
xml = ET.tostring(root, encoding='unicode') + '\n'
Path('representative.reproduced.xml').write_text(xml)
assert xml == (fixture / 'representative.xml').read_text()
# sqlcmd splits long FOR JSON results into rows; join those transport lines.
rows = json.loads(''.join(Path('policy.raw.json').read_text().splitlines()))
Path('database-policy.reproduced.json').write_text(json.dumps(rows, indent=2) + '\n')
assert rows == json.loads((fixture / 'database-policy.json').read_text())
print('Export and independent database rows reproduced exactly')
PY
python3 "$fixture/verify-fixtures.py"
python3 -m json.tool "$fixture/expected-claims.json" > /dev/null
docker stop dms-1617-ods-fixture
```

The pinned SQL orders actions and strategies explicitly, but does not explicitly
order root/child claims or claim sets. The captured ordering reproduced on this
identified engine/seed. A different engine plan may require structural review of
ordering differences; do not silently regenerate an approved expected file.

The unit project copies `Fixtures/**/*` to its output directory, retaining paths.
Review scripts and provenance are not production dependencies.

## Synthetic coverage still required

This seed does not cover name/URI collisions, empty normalized names, 256/257
character limits, controls or non-ASCII set names, case preservation edge cases,
near-match/non-leading legacy prefixes, malformed XML, unknown policy content,
missing names/actions, or documents missing claims/references/actions. It also
does not cover explicit empty default/strategy/override containers or populated
`validationRuleSet`/`validationRuleSetOverride` attributes. Preserve those as small
synthetic tests under DMS-1617-03/-04/-05; do not alter this actual export to add
them. CLI failure/write-order and upload checks remain their respective tasks.
