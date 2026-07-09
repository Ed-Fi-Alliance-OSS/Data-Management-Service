<!-- SPDX-License-Identifier: Apache-2.0 -->
# Clean redeploy runbook (existing VM)

Tear down an existing deployment and do a fresh install on a VM that already has
**WSL2 + Docker + PowerShell 7 + git** and a prior deployment to wipe. This is the condensed
"wipe and redeploy" path; for first-time VM provisioning see [`README.md`](README.md) /
[`windows/README.md`](windows/README.md), and for the full manual walkthrough see
[`MANUAL.md`](MANUAL.md).

> **No host .NET SDK required.** The `api-schema-tools` tool is built in a **container** (Part C),
> so the only host prerequisites are Docker, `pwsh`, and `git` — exactly what the fresh-VM
> setup paths install. (It is also published as the `EdFi.Api.SchemaTools` .NET tool — DMS-1242 —
> which is the simpler route on a host that already has a .NET 10 SDK; the container build keeps
> this VM dependency-free.)

Commands are labeled **[bash]** (WSL shell) or **[pwsh]** (`pwsh` inside WSL). Assumes the repo
is at `~/dms-src` and a public `FQDN` for the VM. Replace `<FQDN>` throughout.

## Part A — Tear down the existing deployment  [bash]

```bash
cd ~/dms-src/eng/azure-vm/compose
./down.sh -v || true                        # stop containers + drop ALL volumes (incl. Keycloak realm)
docker network rm dms-sec 2>/dev/null || true
rm -f .env ssl/server.crt ssl/server.key    # force fresh secrets + cert on next run
rm -rf .bootstrap                            # force fresh ApiSchema staging
# drop old images so the current (renamed) ones are pulled fresh:
docker image rm edfialliance/ed-fi-api:pre edfialliance/ed-fi-api-configuration-service:pre 2>/dev/null || true
docker image rm edfialliance/data-management-service:pre edfialliance/dms-configuration-service:pre 2>/dev/null || true
docker system prune -f
```

## Part B — Check out the revision to deploy  [bash]

```bash
cd ~/dms-src
REF=origin/main   # or a release tag, or the branch under review (e.g. origin/DMS-1196)
git fetch origin --tags
git switch --detach "$REF"
git log -1 --oneline
```

## Part C — Build the schema tool + stage ApiSchema  [bash]

The DMS mounts a staged ApiSchema workspace, and `up.sh` / `setup-env.ps1 -StartDms` **refuse to
start** without it. Build `api-schema-tools` in a container (no host .NET SDK needed), stage the
workspace, then copy it into the azure-vm compose folder.

```bash
cd ~/dms-src/eng/docker-compose

# 1. Build the api-schema-tools tool (self-contained; runs in WSL without a host .NET runtime).
#    --user keeps the bind-mounted output owned by you: published as root, .bootstrap/ would be
#    root-owned on a clean clone and the host-side prepare step below could not write into it.
docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget \
  -v ~/dms-src:/src -w /src/eng/docker-compose mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish ../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj \
  -c Release -r linux-x64 --self-contained -p:UseAppHost=true -o .bootstrap/tools/api-schema-tools

# 2. Stage the DS 5.2 core ApiSchema workspace (downloads the ApiSchema package from the Ed-Fi feed):
pwsh ./prepare-dms-schema.ps1 -SchemaToolPath ./.bootstrap/tools/api-schema-tools/api-schema-tools

# 3. Copy the staged workspace into the azure-vm deployment:
mkdir -p ~/dms-src/eng/azure-vm/compose/.bootstrap
cp -r ./.bootstrap/ApiSchema ~/dms-src/eng/azure-vm/compose/.bootstrap/
ls ~/dms-src/eng/azure-vm/compose/.bootstrap/ApiSchema/*.json   # sanity: should list schema files
```

> The staged ApiSchema must match the populated template (Part E) at the ApiSchema **package
> version** level, not just the Data Standard level: the template restore writes the
> `dms.EffectiveSchema` fingerprint of the ApiSchema it was built from, and the DMS refuses the
> database at startup if the mounted workspace hashes differently (e.g. after an upstream
> ApiSchema package version bump). If startup reports a schema mismatch after a restore, restage
> with `SCHEMA_PACKAGES` pinned to the ApiSchema version the template was built with.

## Part D — Stand up infra + bootstrap (NOT the DMS yet)  [pwsh]

```bash
cd ~/dms-src/eng/azure-vm/compose
pwsh ../provision/setup-env.ps1 -PublicHost <FQDN> -LetsEncryptEmail you@org.tld
# omit -LetsEncryptEmail to use a self-signed cert instead
```

Generates `.env` (secrets, locked to `0600`), obtains the TLS cert, starts PostgreSQL + Keycloak +
both Config Services + gateway, and runs `bootstrap.ps1` (Keycloak realm/clients, tenants, CMS
data stores, review apps). **Record the API key/secret pairs it prints** into your private vault.
It deliberately does **not** start the DMS.

> Don't use `localhost` as the FQDN — CMS calls the public host from inside its container. A
> real FQDN (Let's Encrypt) is the tested path.

## Part E — Seed all three data databases  [bash]

Restore the relational populated template (schema **and** data) into each empty DB:

```bash
cd ~/dms-src/eng/azure-vm/compose
bash ./seed/grandbend.sh edfi_st edfi_mt edfi_mt_t2
```

Gives single-tenant + both tenants an identical, physically-isolated Grand Bend graph — no
separate `api-schema-tools` provisioning needed for these DBs (the template brings its own schema).

> `grandbend.sh` downloads `EdFi.Api.Populated.Template.PostgreSql.5.2.0` from the Ed-Fi Azure
> Artifacts feed and requires a **relational** build; it fails loudly if the feed/package is
> unreachable.

## Part F — Start the DMS services  [pwsh]

```bash
cd ~/dms-src/eng/azure-vm/compose
pwsh ../provision/setup-env.ps1 -PublicHost <FQDN> -StartDms
```

The re-run preserves all secrets and skips bootstrap; it starts `st-dms` / `mt-dms` only after
confirming the staged ApiSchema is present. Each `/health` should go green within a couple of
minutes.

## Part G — Verify  [bash]

```bash
# health:
for p in st-dms st-config mt-dms mt-config; do
  echo -n "$p: "; curl -sk -o /dev/null -w "%{http_code}\n" "https://<FQDN>/$p/health"
done

# end-to-end smoke (tokens + reads all three; exits nonzero on any failure):
cd ~/dms-src/eng/azure-vm/http
FQDN=<FQDN> ST_CREDS='key:secret' T1_CREDS='key:secret' T2_CREDS='key:secret' ./sample-all.sh
```

Use the key/secret pairs from Part D.

---

**Windows + WSL, lost connectivity after a reboot:** re-run
`pwsh ~/dms-src/eng/azure-vm/provision/windows/portproxy.ps1` (or confirm the startup task ran)
to re-point Windows :80/:443 at WSL's current IP.
