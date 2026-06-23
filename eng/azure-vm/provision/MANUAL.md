# Manual deployment (raw commands, no wrapper scripts)

Every step as explicit commands. The wrapper scripts in this folder just automate
these — use whichever you prefer. Replace the placeholders in **Step 0**.

> **Linux-VM path.** The canonical host is Windows + WSL2 — see [`windows/README.md`](windows/README.md)
> (its host setup differs). The in-VM steps here (from Step 5, run inside WSL) are identical.

---

## Step 0 — Choose values (workstation)

```bash
RG=rg-dms-security-review
LOCATION=eastus
VM=dms-sec-vm
DNS_LABEL=your-label          # must be unique within the region
ADMIN_USER=edfi
ADMIN_CIDR=$(curl -s ifconfig.me)/32   # your workstation IP for SSH
APP_CIDR=Internet                    # who can reach 80/443 (Internet for a pen test)
```

Prereqs: `az login` done, an SSH key at `~/.ssh/id_rsa.pub` (`ssh-keygen -t ed25519` if not),
and PowerShell 7 on the workstation.

---

## Step 1 — Resource group (workstation)

```bash
az group create -n "$RG" -l "$LOCATION" -o table
```

---

## Step 2 — Create the VM (workstation)

Render cloud-init with your admin user, then create the VM:

```bash
sed "s/__ADMIN_USER__/$ADMIN_USER/" cloud-init.yaml > /tmp/dms-cloud-init.yaml

az vm create \
  -g "$RG" -n "$VM" \
  --image Ubuntu2404 \
  --size Standard_B2ms \
  --admin-username "$ADMIN_USER" \
  --ssh-key-values ~/.ssh/id_rsa.pub \
  --public-ip-sku Standard \
  --public-ip-address-dns-name "$DNS_LABEL" \
  --storage-sku StandardSSD_LRS \
  --os-disk-size-gb 64 \
  --custom-data /tmp/dms-cloud-init.yaml \
  --nsg-rule NONE \
  -o table
```

> If `--image Ubuntu2404` is rejected, use `--image Canonical:ubuntu-24_04-lts:server:latest`.

---

## Step 3 — NSG inbound rules (workstation)

```bash
NSG=$(az network nsg list -g "$RG" --query "[0].name" -o tsv)

az network nsg rule create -g "$RG" --nsg-name "$NSG" -n allow-ssh \
  --priority 1000 --access Allow --protocol Tcp --direction Inbound \
  --destination-port-ranges 22 --source-address-prefixes "$ADMIN_CIDR" -o none

az network nsg rule create -g "$RG" --nsg-name "$NSG" -n allow-http \
  --priority 1010 --access Allow --protocol Tcp --direction Inbound \
  --destination-port-ranges 80 --source-address-prefixes Internet -o none

az network nsg rule create -g "$RG" --nsg-name "$NSG" -n allow-https \
  --priority 1020 --access Allow --protocol Tcp --direction Inbound \
  --destination-port-ranges 443 --source-address-prefixes "$APP_CIDR" -o none
```

Get the FQDN you'll use everywhere below:

```bash
FQDN=$(az vm show -d -g "$RG" -n "$VM" --query fqdns -o tsv); echo "$FQDN"
```

---

## Step 4 — Wait for cloud-init, then SSH in

```bash
ssh "$ADMIN_USER@$FQDN" "cloud-init status --wait && docker --version && pwsh --version"
ssh "$ADMIN_USER@$FQDN"
```

Everything below runs **on the VM**.

---

## Step 5 — Get the deployment tooling onto the VM

The tooling lives in the (public) Data-Management-Service repo under `eng/azure-vm/` — which you
also need for the source review and the `dms-schema` build:

```bash
git clone https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service.git ~/dms-src
cd ~/dms-src/eng/azure-vm/compose
```

---

## Step 6 — Create and fill in `.env` (on the VM)

```bash
cp .env.example .env
FQDN=<paste-the-FQDN-from-step-3>    # e.g. your-label.eastus.cloudapp.azure.com

# Helper recipes (each meets the client-secret complexity rules):
gen()  { echo "$(openssl rand -hex 16)Aa1!"; }   # 36 chars: lower+upper+digit+special
key32(){ openssl rand -hex 16; }                 # exactly 32 chars
b64()  { openssl rand -base64 32; }

# Write values (edit with nano instead if you prefer):
sed -i "s#^PUBLIC_BASE_URL=.*#PUBLIC_BASE_URL=https://$FQDN#"                 .env
sed -i "s#^PUBLIC_HOST=.*#PUBLIC_HOST=$FQDN#"                                  .env
sed -i "s#^POSTGRES_PASSWORD=.*#POSTGRES_PASSWORD=$(gen)#"                     .env
sed -i "s#^KEYCLOAK_ADMIN_PASSWORD=.*#KEYCLOAK_ADMIN_PASSWORD=$(gen)#"         .env
sed -i "s#^DMS_CONFIG_IDENTITY_CLIENT_SECRET=.*#DMS_CONFIG_IDENTITY_CLIENT_SECRET=$(gen)#" .env
sed -i "s#^CONFIG_SERVICE_CLIENT_SECRET=.*#CONFIG_SERVICE_CLIENT_SECRET=$(gen)#"           .env
sed -i "s#^BOOTSTRAP_ADMIN_CLIENT_SECRET=.*#BOOTSTRAP_ADMIN_CLIENT_SECRET=$(gen)#"         .env
sed -i "s#^PGADMIN_DEFAULT_PASSWORD=.*#PGADMIN_DEFAULT_PASSWORD=$(gen)#"       .env
sed -i "s#^DMS_CONFIG_DATABASE_ENCRYPTION_KEY=.*#DMS_CONFIG_DATABASE_ENCRYPTION_KEY=$(key32)#" .env
sed -i "s#^DMS_CONFIG_IDENTITY_ENCRYPTION_KEY=.*#DMS_CONFIG_IDENTITY_ENCRYPTION_KEY=$(b64)#"   .env

grep -E '^(PUBLIC_|POSTGRES_PASSWORD|.*SECRET|.*ENCRYPTION_KEY)' .env   # sanity check
```

Record these values in your **private** deployment doc / vault — **not** in this folder (it's
part of the public Data-Management-Service repo).

---

## Step 7 — TLS certificate (on the VM)

**Let's Encrypt** (port 80 must be open and the stack not yet running):

```bash
sudo certbot certonly --standalone --non-interactive --agree-tos -m you@org.tld -d "$FQDN"
sudo cp /etc/letsencrypt/live/$FQDN/fullchain.pem ssl/server.crt
sudo cp /etc/letsencrypt/live/$FQDN/privkey.pem   ssl/server.key
sudo chown "$USER" ssl/server.crt ssl/server.key
```

Or **self-signed** (testing): `./ssl/generate-certificate.sh "$FQDN"`

---

## Step 8 — Start the stack (on the VM)

```bash
docker network create dms-sec
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d
docker compose -f docker-compose.yml -f keycloak.yml --env-file .env ps
```

Wait for health:

```bash
for p in st-dms st-config mt-dms mt-config; do
  curl -sk -o /dev/null -w "$p %{http_code}\n" "https://localhost/$p/health"
done
# NOTE: st-dms/mt-dms will NOT be healthy yet — they crash-loop until Step 9 (bootstrap)
# creates the Keycloak realm + data stores, AND the relational schema must already be
# provisioned out of band (dms-schema). st-config/mt-config should reach 200. See
# provision/README.md "Known gaps" #1 and #3.
```

---

## Step 9 — Bootstrap clients, tenants, data stores (on the VM)

This step is inherently scripted (Keycloak realm/clients + CMS vendor/application/tenant/
data-store API calls). Run it over the loopback:

```bash
pwsh ./bootstrap/bootstrap.ps1 -BaseUrl https://localhost -Insecure
```

It prints the **API key/secret** for the single-tenant app and each tenant — copy them into
`docs/infrastructure.md`.

---

## Step 10 — Verify

```bash
curl -sk https://localhost/st-dms | head        # Discovery JSON
# from your workstation/browser:
#   https://$FQDN/                (landing page)
#   https://$FQDN/st-dms , /mt-dms , /st-config , /mt-config , /auth , /pgadmin
```

Then run the requests in `../http/`.

---

## Day-to-day: stop / start (workstation, saves cost)

```bash
az vm deallocate -g "$RG" -n "$VM"    # stop COMPUTE billing when idle
az vm start      -g "$RG" -n "$VM"    # before a session; containers auto-resume from disk
```

`deallocate` is what stops compute charges (a plain shutdown does not). The static IP, FQDN,
and TLS cert all survive, and no re-bootstrap is needed.

---

## Update / reset / teardown

```bash
# Update in place (on the VM):
cd ~/dms-src/eng/azure-vm/compose && git pull && \
  docker compose -f docker-compose.yml -f keycloak.yml --env-file .env pull && \
  docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d

# Data reset, keep Keycloak realm (on the VM):
docker compose -f docker-compose.yml --env-file .env down -v && \
  docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d && \
  pwsh ./bootstrap/bootstrap.ps1 -BaseUrl https://localhost -Insecure

# Tear everything down (workstation):
az group delete -n "$RG" --yes
```
