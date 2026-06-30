<!-- SPDX-License-Identifier: Apache-2.0 -->
# Azure VM deployment — DMS single-tenant + multi-tenant

Deploys two Ed-Fi DMS environments — **single-tenant** and **multi-tenant** (two isolated
tenants) — behind one NGINX gateway on a single Azure VM (**Windows host running Linux
containers via WSL2**), relational backend, Keycloak identity. Built for a third-party
security review; generic enough for any ST + MT-behind-a-gateway VM deployment.

> ⚠️ This folder is **secret-free**. Per-deployment specifics (VM, API keys, `.env` secrets)
> belong in that deployment's **private** credentials doc / vault — never commit them here.

## Reuses the repo bootstrap (no duplication)

- `compose/bootstrap/bootstrap.ps1` imports the canonical `eng/Dms-Management.psm1` and
  `eng/docker-compose/setup-keycloak.ps1` — it does **not** vendor copies.
- For a standard single-stack bring-up + relational populated-template seeding, prefer the
  official `eng/docker-compose/bootstrap-published-dms.ps1` (e.g. `-SeedTemplate Populated`,
  the DMS-1159 relational template path). This folder adds only the delta that flow doesn't
  cover: the **two-stack + gateway** topology, the **multi-tenant** tenants, and the named
  **review applications**.

## Layout

| Path | Purpose |
|------|---------|
| `provision/` | Azure VM lifecycle + host setup. **Windows + WSL2 is canonical** (`provision/windows/`); the Linux/cloud-init flow is an alternative. |
| `compose/` | Two-stack `docker-compose.yml`, NGINX gateway, `keycloak.yml`, PostgreSQL init, seed (`grandbend.sh` relational template restore, `clone-data.sh` MT clone), and `bootstrap/bootstrap.ps1`. |
| `http/` | REST Client walkthroughs + `sample-all.sh` smoke sampler (placeholders). |
| `docs/infrastructure.md` | Architecture, endpoints, provisioning method, known issues. |

## Quick start

See `provision/README.md` (canonical host: Windows + WSL2). In short: provision the VM →
WSL2 + Docker → copy this folder onto the VM → `provision/setup-env.ps1` (secrets, cert,
identity/CMS, bootstrap; the DMS services start after schema provisioning) → exercise the API
with `http/`. Capture the generated credentials in your private deployment doc.
