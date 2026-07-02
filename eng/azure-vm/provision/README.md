<!-- SPDX-License-Identifier: Apache-2.0 -->
# Azure VM deployment runbook

Stand up the two-stack (single-tenant + multi-tenant) DMS environment on one Azure VM, with
deallocate-when-idle cost control. The deployment tooling is this `eng/azure-vm/` folder in the
Data-Management-Service repo — clone that repo onto the VM (you need it for the source review and
the `dms-schema` build anyway) and work from `eng/azure-vm/`.

> Credentials and per-deployment specifics are **not** stored here — keep them in a private
> deployment doc / vault (see the [folder README](../README.md)).

## Canonical host: Windows + WSL2

The reference deployment is a **Windows Server VM running the Linux containers in WSL2**. Follow
**[`windows/README.md`](windows/README.md)** — the full runbook: create the Windows VM (Portal) →
DNS label → NSG (RDP / 80 / 443) → RDP in → `wsl --install -d Ubuntu` →
`windows/setup-windows-host.ps1` (WSL networking, firewall, Docker/pwsh/git/certbot in WSL, boot
autostart) → then the **shared bring-up** below.

## Alternative host: Linux VM (Ubuntu + cloud-init)

Fewer moving parts if you don't need a Windows host. From your workstation (needs Azure CLI +
`az login`, PowerShell 7, an SSH key, and your admin IP CIDR):

```powershell
cd eng/azure-vm/provision
./provision-vm.ps1 -DnsLabel <your-label> -AllowedSshCidr <your-ip>/32 -Location eastus -ResourceGroup rg-dms-security-review
```

Creates an Ubuntu 24.04 B2ms VM + static public IP + DNS label + NSG; cloud-init installs
Docker/pwsh/git/certbot. Browser-only equivalent: [`PORTAL.md`](PORTAL.md). Then
`ssh edfi@<FQDN> "cloud-init status --wait"`.

## Shared bring-up (inside WSL on Windows, or on the Linux VM)

```bash
git clone https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service.git ~/dms-src
cd ~/dms-src/eng/azure-vm/compose
pwsh ../provision/setup-env.ps1 -PublicHost <FQDN> -LetsEncryptEmail you@org.tld
```

`setup-env.ps1` generates secrets into `.env`, obtains the TLS cert (omit `-LetsEncryptEmail` for
self-signed), starts identity + CMS, and runs `bootstrap.ps1` (Keycloak realm/clients, tenants,
data stores, the review apps). It does **not** start the DMS services — stage the ApiSchema
workspace and provision the relational schema (below), then start them with
`setup-env.ps1 -StartDms` (or `./up.sh st-dms mt-dms`); both refuse to start the DMS services
while `compose/.bootstrap/ApiSchema` is unstaged.
Record the generated credentials in your private deployment doc, then verify with [`../http/`](../http/).

Re-running `setup-env.ps1` is safe: secrets are written only when `.env` is first created (pass
`-RotateSecrets` to force-rotate — and then reset the dependent volumes), and bootstrap runs only
on first stand-up (pass `-Bootstrap` to force). The `-StartDms` pass preserves every secret and
skips bootstrap, so it only starts the DMS services.

> **The relational schema provisioning and Grand Bend seeding are manual** — `setup-env.ps1` does
> not do them. See [`../docs/infrastructure.md`](../docs/infrastructure.md#provisioning-method-as-deployed)
> for the order: provision each data DB with `dms-schema` (or restore the relational populated
> template), bootstrap, start, then seed (single-tenant via bulk-load; the tenants via
> [`../compose/seed/clone-data.sh`](../compose/seed/clone-data.sh)).

## On/off, update, teardown

- **Stop billing:** Portal **Stop (deallocate)** (an in-OS shutdown does **not** stop compute),
  or `provision/stop-vm.ps1` (Linux). **Start** to resume — containers auto-resume (Windows: the
  startup task boots WSL→Docker; if not, RDP in and run `wsl`). Rough cost: B2ms ≈ \$60/mo if
  always on, ≈ \$5–10/mo mostly deallocated (a Windows host costs more).
- **Update (in WSL / on the VM):**
  `cd ~/dms-src/eng/azure-vm/compose && git pull && docker compose -f docker-compose.yml -f keycloak.yml --env-file .env pull && docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d`
- **Cert renewal:** `pwsh provision/renew-cert.ps1 -PublicHost <FQDN>`.
- **Teardown:** `provision/teardown-vm.ps1` (or delete the resource group in the Portal).

## What `setup-env.ps1` does NOT do (provisioning notes)

`setup-env.ps1` automates only the VM-side secrets/cert/start/bootstrap. The rest is manual per
[`../docs/infrastructure.md`](../docs/infrastructure.md#provisioning-method-as-deployed):

- **ApiSchema workspace** — the DMS services mount `compose/.bootstrap/ApiSchema` read-only at
  `/app/ApiSchema` and read the API schema exclusively from there. Stage it with
  `eng/docker-compose/prepare-dms-schema.ps1` (writes `eng/docker-compose/.bootstrap/ApiSchema`;
  copy that folder to `compose/.bootstrap/ApiSchema`). `-StartDms` and `up.sh` refuse to start the
  DMS services while it is unstaged — Docker would otherwise mount it empty and DMS would fail
  startup/health even with the databases provisioned.
- **Relational schema** — the DMS runs `DeployDatabaseOnStartup=false`; provision each data DB with
  `dms-schema` (build-from-source; publishing it is **OPT-2**, unfiled), **or** restore the relational
  populated template (**DMS-1159**, e.g. `eng/docker-compose/bootstrap-published-dms.ps1 -SeedTemplate Populated`).
- **Startup order** — the DMS fail-fast crash-loops until Keycloak + CMS data stores exist
  (**DMS-1093 / DMS-1109**); `setup-env.ps1` therefore bootstraps first and starts the DMS only
  with `-StartDms` (after the schema is provisioned).
- **Multi-tenant seeding** — `DMS-1230` (the MT XSD 404) is fixed in `:pre` ≥ 2026-06-24, so MT can
  be API-seeded directly, or use `seed/clone-data.sh` (faster). Generated client secrets are now
  Basic/form-safe by construction (**DMS-1231**, fixed) — `docker compose … pull` to get both.
