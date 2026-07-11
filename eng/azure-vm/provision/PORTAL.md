# Provision via the Azure Portal (web browser)

Browser-only equivalent of `provision-vm.ps1`. After the VM exists you connect to it
in the browser (Cloud Shell or Bastion) and run the in-VM steps from
[`MANUAL.md`](MANUAL.md) Steps 5–10.

> This is the **Linux VM** alternative. The canonical host is Windows + WSL2 — see
> [`windows/README.md`](windows/README.md), which has its own Portal creation steps.

Values used below (change as needed):
`rg-dms-security-review` · region `East US` · VM `dms-sec-vm` · size `Standard_B2ms` ·
admin user `edfi` · DNS label `your-label`.

---

## 1. Resource group

1. <https://portal.azure.com> → search **Resource groups** → **+ Create**.
2. Subscription = yours; **Resource group** = `rg-dms-security-review`; **Region** = East US.
3. **Review + create** → **Create**.

---

## 2. Create the virtual machine

Search **Virtual machines** → **+ Create** → **Azure virtual machine**.

### Basics
- **Resource group:** `rg-dms-security-review`
- **Virtual machine name:** `dms-sec-vm`
- **Region:** East US
- **Security type:** leave default (Trusted launch) — cloud-init works fine
- **Image:** Ubuntu Server **24.04 LTS** – x64 Gen2
- **Size:** **See all sizes** → search `B2ms` → select **Standard_B2ms**
- **Authentication type:** **SSH public key**
- **Username:** `edfi`
- **SSH public key source:**
  - *Use existing public key* → paste the contents of your `~/.ssh/id_rsa.pub`, **or**
  - *Generate new key pair* (name it `dms-sec-vm_key`) — you'll download the private key at the end
- **Public inbound ports:** **None** (we add precise NSG rules in Step 4)

### Disks
- **OS disk type:** **Standard SSD (locally-redundant)** (cheaper; fine for this workload)
- OS disk size: **64 GiB** (matches the scripted path in `provision-vm.ps1` / `MANUAL.md`;
  gives headroom for images, the ApiSchema workspace, and the populated template).

### Networking
- **Virtual network / Subnet:** accept the auto-created defaults.
- **Public IP:** click **Create new** (or edit) → **SKU = Standard**, **Assignment = Static**.
- **NIC network security group:** **Basic**, **Public inbound ports = None**
  (a new NSG `dms-sec-vmNSG` is created; we add rules next).

### Advanced  ← important (cloud-init)
- Paste this into **Custom data** (installs Docker, PowerShell, git, certbot on first boot):

  ```yaml
  #cloud-config
  package_update: true
  packages:
    - ca-certificates
    - curl
    - git
    - apt-transport-https
  runcmd:
    - curl -fsSL https://get.docker.com | sh
    - usermod -aG docker edfi
    - systemctl enable --now docker
    - snap install powershell --classic
    - snap install certbot --classic
    - ln -sf /snap/bin/certbot /usr/local/bin/certbot
    - touch /var/lib/cloud/dms-sec-provisioned
  ```

  > If your admin username isn't `edfi`, change it in the `usermod` line.

### Review + create
- **Review + create** → **Create**. If you generated a new key pair, **Download private key**
  and keep it safe.

---

## 3. Set the DNS name label

1. Go to the **Public IP** resource (search the VM name; its public IP is `dms-sec-vmPublicIP`
   or similar) → **Settings → Configuration**.
2. **DNS name label:** `your-label` → **Save**.
3. Your FQDN is now **`your-label.eastus.cloudapp.azure.com`** — use it everywhere below.

---

## 4. Add NSG inbound rules

VM → **Networking** (a.k.a. *Network settings*) → **Add inbound port rule** (do all three):

| Name | Source | Source IP/range | Dest port | Protocol | Priority |
|------|--------|-----------------|-----------|----------|----------|
| `allow-ssh` | IP Addresses | your IP `/32` | 22 | TCP | 1000 |
| `allow-http` | Any | — | 80 | TCP | 1010 |
| `allow-https` | Any (or tester IPs) | — | 443 | TCP | 1020 |

(80 must be open for Let's Encrypt + the HTTP→HTTPS redirect; restrict 443 to the testers'
ranges instead of *Any* if you prefer.)

---

## 5. Connect to the VM in the browser

Wait ~2–3 min for cloud-init to finish, then use either:

**Azure Cloud Shell** (top bar `>_` icon → Bash):
```bash
# upload your SSH PRIVATE key via Cloud Shell's Upload button, then:
chmod 600 dms-sec-vm_key.pem
ssh -i dms-sec-vm_key.pem edfi@your-label.eastus.cloudapp.azure.com
# verify cloud-init finished:
cloud-init status --wait && docker --version && pwsh --version
```

**Azure Bastion** (no key handling in the shell; small extra cost):
VM → **Connect → Bastion** → username `edfi`, upload your SSH private key → **Connect**
opens an in-browser terminal.

---

## 6. Finish setup (in the VM shell)

Run **Steps 5–10 of [`MANUAL.md`](MANUAL.md)**: clone the repo (deploy key), fill `.env`,
get the TLS cert, bring up identity/CMS, run `bootstrap.ps1`, then provision the schema and
start the DMS services. Those steps are identical regardless of how you provisioned.

---

## On/off and teardown — also in the Portal

- **Stop billing when idle:** VM → **Stop**. The Portal's *Stop* **deallocates** the VM
  (stops compute charges) — unlike shutting down from inside the OS. **Start** to resume;
  containers come back automatically.
- **Tear down:** Resource group → **Delete resource group**.
