# Trust parity fixture (DMS-1438, Step 2.1)

Produced once by the delivered DMS-1271 PowerShell tooling, so the C# trust policy reader and attestation
verifier are proven against real tooling output rather than against documents the C# tests wrote themselves.

- `template-trust-policy.json`: the policy `eng/DatabaseTemplates/new-template-dev-trust.ps1 -Purpose Dev`
  wrote (`-ProducerName dms-1438-parity`, with `-KeyDirectory` and `-LocalPolicyPath` pointed at a scratch
  directory outside the repository).
- `EdFi.Api.Minimal.Template.PostgreSql.5.2.0.0.0.1-dms1438-parity.nupkg`: a tiny stand-in package (a zip
  archive with one placeholder entry), not a database template.
- `EdFi.Api.Minimal.Template.PostgreSql.5.2.0.0.0.1-dms1438-parity.nupkg.attestation.json`: written by
  `Write-TemplatePackageAttestation` (`eng/DatabaseTemplates/Template-Management.psm1`) over that package with
  the generated key.

The signing key was deleted after generation and is not committed; regenerating the fixture means regenerating
all three files together. The two JSON documents were normalized to LF line endings (the repository's text
rule); the signature covers the decoded payload bytes, so that does not affect verification.

`TemplateAttestationVerifierTests.Given_the_PowerShell_produced_parity_fixture` consumes these files.
