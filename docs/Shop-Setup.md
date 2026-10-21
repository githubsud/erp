# Setting up a shop computer

After this, the ERP starts by itself whenever the computer is switched on, even if nobody signs in to Windows. After a
power cut, the till and phones work again about a minute after the computer is back on.

## What you need

- A Windows 10/11 computer (a mini PC is enough; 8 GB of memory is plenty) connected to the shop's internet.
- The ERP package: on your laptop run `powershell -ExecutionPolicy Bypass -File scripts\package.ps1`, and copy
  `dist\erp-package-<version>.zip` to a USB stick.
- Your Tailscale account (see [Tailscale.md](Tailscale.md)) and a USB disk or OneDrive folder for the second backup copy.

## Steps (about 20 minutes)

1. **Tailscale**: install it from tailscale.com/download, sign in with your account, and in the admin console
   (Machines → this computer → ⋯) choose **Disable key expiry**.
2. **Unzip** the package to a folder, for example `C:\ERP-setup`.
3. Open **PowerShell as Administrator** (Start → type PowerShell → right-click → Run as administrator) and run:

   ```powershell
   cd C:\ERP-setup
   powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -AdminEmail you@example.com -AdminPassword 'Your-Strong-Password' -BackupCopyTo 'E:\ERP-backups'
   ```

   It installs PostgreSQL 17 (if missing), the database service **ErpDatabase**, the ERP service **ErpApi**, the
   nightly backup, and publishes the ERP on Tailscale. At the end it prints the address and **two keys**.
4. **Save the two keys** in your password manager straight away (not on the same disk as the backups).
5. Open the printed address on your phone → `/backoffice` → sign in with the email and password from step 3 →
   **Customers → New** to create the shop, its owner login and plan.
6. On the till (and the owner's phone): install Tailscale, open the address, sign in as the shop owner, and install the
   app (see Tailscale.md).
7. **Test a power cut**: restart the computer, do not sign in, and check from your phone that the ERP opens within a
   couple of minutes.

## Updating

Copy the newer package to the computer, unzip it, and run `scripts\install.ps1` again as Administrator (no other options
needed). The program is replaced; data, passwords and keys stay. Database changes are applied as the service starts.

## Where things are

| | |
|---|---|
| `C:\ERP\app` | the program and screens (replaced on update) |
| `C:\ERP\config\erp.json` | passwords and keys (administrators and the ERP service only) |
| `C:\ERP\pgdata` | the database (service ErpDatabase, port 5434, this computer only) |
| `C:\ERP\backups` | nightly backups and `backup.log` |
| `C:\ERP\scripts` | backup.ps1, restore.ps1, install.ps1 |

Problems: Windows **Services** shows ErpApi and ErpDatabase (both should be *Running*); the ERP's warnings and errors are
in **Event Viewer → Windows Logs → Application**, source *Erp.Api*. `scripts\install.ps1 -Uninstall` removes the two
services but keeps the data.

## Moving existing data

To bring data from another computer, take a backup there and copy its settings file (it holds the keys the data is
encrypted with), then install with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -ImportDump erp-<date>.dump -ImportKeysFrom erp.json
```
