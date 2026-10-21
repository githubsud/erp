# Reaching the ERP from phones and other places (Tailscale)

Tailscale puts your devices (the shop computer, the till, the owner's phone, your laptop at home) on one private
network. The ERP gets a private https address such as `https://shop1.tail1234.ts.net` that only those devices can open.
Nothing is opened to the public internet and the shop's router needs no changes. Free for personal use (3 users, 100 devices).

The API also serves the built web app (`src/Erp.Web/dist`), so one address carries both and Tailscale forwards only port 5080.

## Once per account

1. Create an account at https://login.tailscale.com (sign in with Google or Microsoft).
2. Open https://login.tailscale.com/admin/dns and switch on **MagicDNS** and **HTTPS Certificates**.
   Phones only install the app from an https address.

## On the computer that runs the ERP

```powershell
powershell -ExecutionPolicy Bypass -File scripts\tailscale.ps1 -Install   # Windows asks for permission: click Yes
powershell -ExecutionPolicy Bypass -File scripts\tailscale.ps1            # signs in (browser), publishes, prints the address
```

The script builds the web app when it is missing (`-Build` rebuilds it after an update) and publishes
`http://localhost:5080` in the background. It stays published after restarts until you run `scripts\tailscale.ps1 -Off`.
The ERP itself must be running (`dotnet run --project src/Erp.Api`).

## On each phone, till or laptop

1. Install the **Tailscale** app (Play Store, App Store, or tailscale.com/download) and sign in to the same account.
   For the shop owner's own phone you can instead invite them: admin console → **Users** → **Invite**.
2. Open the address the script printed, in Chrome (Android) or Safari (iPhone).
3. Menu → **Install the app** (Android/Chrome) or Share → **Add to Home Screen** (iPhone).
4. Sign in with "Keep me signed in on this device" ticked.

## Good to know

- Each device must have Tailscale switched on to reach the ERP.
- During an internet cut the till keeps selling and syncs later. Devices on the shop's Wi-Fi that were already
  connected usually keep reaching the shop computer, because Tailscale talks to it directly on the local network;
  phones outside the shop cannot reach it until the internet is back.
- To remove a lost phone: admin console → **Machines** → the phone → **Remove**. Also sign the user out in the ERP.
- Keys expire after 180 days by default. For the shop computer, admin console → **Machines** → … → **Disable key expiry**,
  or it drops off the network one day.
