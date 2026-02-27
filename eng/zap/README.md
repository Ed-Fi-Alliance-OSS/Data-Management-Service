# ZAP runner for DMS/CMS

This folder contains a convenience script to run OWASP ZAP against the DMS and CMS OpenAPI specs.

Script: `Invoke-ZapDmsCms.ps1` — creates temporary test clients, fetches OpenAPI specs, updates localhost hostnames for container use, and runs `zap-api-scan.py` in a ZAP Docker image.

Quick notes

- Prerequisites: `docker` on PATH and PowerShell (Windows/Linux/macOS). The script will fail if Docker is not available.
- Default output: reports are saved to the `zap-reports` directory (or the directory passed via `-OutputDir`).

Usage

PowerShell example (defaults):

```powershell
.\Invoke-ZapDmsCms.ps1
```

Common parameters

- `-DmsBaseUrl` (default `http://localhost:8080`) — DMS base URL.
- `-CmsBaseUrl` (default `http://localhost:8081`) — Configuration Service base URL.
- `-OutputDir` (default `./zap-reports`) — directory where reports/specs are written.
- `-SysAdminId` / `-SysAdminSecret` — credentials used to obtain a CMS admin token (defaults match the local dev stack).
- `-ZapImage` — Docker image used for ZAP (default `ghcr.io/zaproxy/zaproxy:stable`).
- `-HostAlias` — host name substituted into OpenAPI specs so the ZAP container can reach services (default `host.docker.internal`).
- `-IgnoreZapExitCode` — pass this switch to avoid non-zero ZAP exit codes causing script failure (useful for CI when you only want reports saved).

What the script does

- Validates Docker availability.
- Requests a CMS admin token using the provided admin client credentials.
- Provisions a temporary vendor, DMS instance and application via CMS to obtain a DMS client for scanning.
- Requests a DMS token from the DMS discovery endpoint and confirms API access.
- Downloads the DMS resources/descriptors OpenAPI specs and the CMS OpenAPI spec to the output directory.
- Rewrites `localhost` hostnames in the specs to `-HostAlias` so the ZAP Docker container can reach the services.
- Runs `zap-api-scan.py` inside the configured ZAP Docker image three times (resources, descriptors, cms), producing HTML/JSON/XML reports prefixed with `dms-resources`, `dms-descriptors`, and `cms`.

Outputs

- `<OutputDir>/dms-resources.html|.json|.xml`
- `<OutputDir>/dms-descriptors.html|.json|.xml`
- `<OutputDir>/cms.html|.json|.xml`

Tips and safety

- The script provisions temporary resources in the CMS (vendor, instance, application). These are intended for test runs and can be cleaned up after.
- The script contains sensible defaults for a local developer environment; pass explicit parameters in CI or non-standard setups.
- When triaging ZAP findings, use the generated JSON files in the output directory (`*.json`) for targeted analysis.

See also

- Script: [eng/zap/Invoke-ZapDmsCms.ps1](eng/zap/Invoke-ZapDmsCms.ps1)
