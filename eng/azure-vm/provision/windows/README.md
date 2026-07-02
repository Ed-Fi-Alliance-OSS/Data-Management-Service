# Windows host variant (RDP) — DMS still runs in Linux containers

The team RDPs into a **Windows Server** VM; the DMS stack stays as **Linux containers**
running inside **WSL2 (Ubuntu)** on that VM. You manage Docker from the WSL terminal.

## Read this first — the trade-offs

Putting Linux containers on a Windows host adds a translation layer (WSL2), and two
parts need care that the plain Linux VM doesn't:

1. **Inbound 443/80 cross the WSL boundary.** WSL2 has its own NAT'd IP, so the public
   port has to reach the service inside WSL. Two ways:
   - **WSL mirrored networking** (clean, no port mapping) — needs **Windows Server 2025**
     (or Windows 11 22H2+). **Recommended.**
   - **`netsh portproxy`** (for **Windows Server 2022**) — forwards Windows:443 → the WSL IP,
     and must be refreshed on boot because the WSL IP changes (handled by `portproxy.ps1`
     + a startup task).
2. **Headless autostart.** WSL/Docker don't start on boot by themselves. We add a startup
   task; if containers don't return after a Start, RDP in and run `wsl` once.

Also: a Windows VM costs more (license + a larger size). **Recommend Windows Server 2025 +
mirrored networking + Standard_D8s_v4 (8 vCPU / 32 GiB)** to match the reference ODS box (a
lighter Standard_B4ms / 16 GiB also runs the DMS stack fine if cost matters). Deallocate when
idle so the larger size only costs you during sessions. This path has more to validate than the
Linux VM; smoke-test before handoff.

> If RDP is only about avoiding SSH-key setup, the `Linux VM + xRDP` option is far less
> moving parts. This doc assumes you've decided on a Windows host.

---

## 1. Provision the Windows VM (Azure Portal)

Search **Virtual machines → + Create → Azure virtual machine**.

- **Resource group:** `rg-dms-security-review` · **Name:** `dms-sec-vm` · **Region:** East US
- **Image:** **Windows Server 2025 Datacenter** (Gen2). *(2022 works; you'll use portproxy.)*
- **Size:** **Standard_D8s_v4** (8 vCPU / 32 GiB — matches the reference ODS VM; B4ms is enough for DMS)
- **Authentication:** Username + **Password** (this is your RDP login). Save it.
- **Public inbound ports:** **None** (we add NSG rules in step 3)
- **Disks:** OS disk **Standard SSD**
- **Networking:** default VNet; **Public IP → SKU Standard, Assignment Static**
- Windows has no cloud-init — setup is done after RDP (steps 5+).
- **Review + create → Create.**

## 2. DNS name label

Public IP resource → **Configuration** → **DNS name label** = `your-label` → **Save**.
FQDN: `your-label.eastus.cloudapp.azure.com`.

## 3. NSG inbound rules

VM → **Networking → Add inbound port rule** (note **RDP**, not SSH):

| Name | Source | Port | Priority |
|------|--------|------|----------|
| allow-rdp | Any (see note) | 3389 | 1000 |
| allow-http | Any | 80 | 1010 |
| allow-https | Any | 443 | 1020 |

> **RDP exposure.** The reference ODS VM leaves 3389 open any→any, relying on the VM being
> deallocated except during sessions. That caps *total* exposure, but during an on-window an open
> 3389 is fully internet-reachable (bots scan Azure ranges within minutes; the static IP + DNS
> label are targetable whenever the VM is up). Safer options, same workflow:
> - admins on stable IPs → set the **Source** to their CIDRs;
> - rotating admin IPs → use **Just-in-Time VM access** (Defender for Cloud) — opens 3389 on
>   demand to the requester's current IP only — or **Bastion**.
>
> If you keep it open to match the reference, use a strong, unique local-admin password and leave
> Network Level Authentication (NLA) enabled.

## 4. RDP in (admin access)

Connect with your RDP tool to the FQDN using the **Windows local admin username/password** you
set at VM creation — that *is* the host credential (the reference uses native RDP this way).
For multiple admins, either share that local admin login (what the reference effectively does),
or give each person their own: add local Windows users, or enable **Entra ID** login —
`az vm extension set --publisher Microsoft.Azure.ActiveDirectory --name AADLoginForWindows -g rg-dms-security-review --vm-name dms-sec-vm`
plus the *Virtual Machine Administrator Login* role per admin (per-user, MFA, revocable).
Testers never RDP — they reach the app only over HTTPS.

## 5. Install WSL2 + Ubuntu (elevated PowerShell on the VM)

```powershell
wsl --install -d Ubuntu
# If that errors on an older Server build, enable features manually then retry:
#   dism /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart
#   dism /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart
#   (reboot) ; wsl --set-default-version 2 ; wsl --install -d Ubuntu
```

**Reboot** when prompted. After reboot, launch **Ubuntu** once (Start menu or `wsl`) to create a
UNIX username/password, then confirm:

```powershell
wsl --list --verbose      # Ubuntu should show VERSION 2
```

## 6. Configure the Windows host (elevated PowerShell on the VM)

Copy `setup-windows-host.ps1` and `portproxy.ps1` onto the VM first (RDP clipboard /
drive redirection, or install Git for Windows and clone). Then, from that folder in an
elevated PowerShell:

```powershell
# Windows Server 2025 (mirrored networking — recommended):
.\setup-windows-host.ps1

# Windows Server 2022 (portproxy fallback):
.\setup-windows-host.ps1 -UsePortProxy
```

This writes `.wslconfig`, opens the Windows firewall for 80/443, installs Docker + PowerShell +
git + certbot **inside WSL**, enables systemd in WSL, and registers a startup task so WSL/Docker
come back after a reboot (plus portproxy refresh in `-UsePortProxy` mode).

## 7. Set up the DMS environment (inside WSL)

Open Ubuntu (WSL) and run the **same** flow as the Linux VM:

```bash
# The DMS repo carries the deployment tooling (and the source you're reviewing):
git clone https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service.git ~/dms-src
cd ~/dms-src/eng/azure-vm/compose
pwsh ../provision/setup-env.ps1 -PublicHost your-label.eastus.cloudapp.azure.com -LetsEncryptEmail you@org.tld
```

`setup-env.ps1` generates secrets into `.env`, gets the Let's Encrypt cert, starts identity + CMS,
and runs bootstrap; it does **not** start the DMS services — stage the ApiSchema workspace and
provision the relational schema (see [`README.md`](../README.md) "What `setup-env.ps1` does NOT do"),
then start them with `setup-env.ps1 -StartDms` (or `./up.sh st-dms mt-dms`). (The Let's Encrypt HTTP-01
check reaches certbot in WSL through the networking you configured in step 6.) See
[`../MANUAL.md`](../MANUAL.md) Steps 6–10 if you'd rather run it by hand.

## 8. Verify

Browse `https://<FQDN>/` and exercise the files in [`../../http/`](../../http/).

## Stop / start (cost control)

Portal → VM → **Stop** deallocates (stops compute **and** Windows license billing); **Start** to
resume. After a Start, the step-6 startup task boots WSL → systemd → Docker → containers. **Verify
this works on your build**; if not, RDP in and run `wsl` once (and `portproxy.ps1` if not mirrored).

## Update / teardown

```bash
# inside WSL:
cd ~/dms-src/eng/azure-vm/compose && git pull && \
  docker compose -f docker-compose.yml -f keycloak.yml --env-file .env pull && \
  docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d
```

Teardown: Portal → delete the resource group.
