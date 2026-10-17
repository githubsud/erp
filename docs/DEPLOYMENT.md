# Deployment guide

Target set-up (from the blueprint): **API + PostgreSQL in Azure Qatar Central** (data stays in Qatar), **web app on
Cloudflare Pages** (free tier is enough to start), Cloudflare DNS/WAF in front of the API. A cheap **staging** copy can
run on Render (Frankfurt) with sample data only — never real payroll data outside Qatar.

## 1. Database (Azure Database for PostgreSQL – Flexible Server, Qatar Central)

1. Create a PostgreSQL **17** flexible server in *Qatar Central*. Start with *Burstable B2s*; production with real
   customers: *General Purpose D2ds_v5* + zone-redundant high availability. Backups: 35 days, geo-redundant off
   (keeps data in-country).
2. Create the database `erp`. The admin user is the **owner** account used only for migrations.
3. Network: private access (VNet) or firewall rule for the API's outbound IP only.

The migrator creates the two runtime roles itself:

| Role | Used by | Rights |
|---|---|---|
| owner (admin) | `--migrate-only` release step | DDL |
| `erp_app` | the API | data only; no table ownership, **no BYPASSRLS**, ledgers insert-only |
| `erp_worker` | outbox dispatcher | reads/updates `core.outbox_message` only |

## 2. Secrets (Azure Key Vault → container environment variables)

| Variable | Notes |
|---|---|
| `Database__OwnerConnectionString` | migration job only |
| `Database__AppConnectionString` | `Username=erp_app;…;SSL Mode=Require` |
| `Database__WorkerConnectionString` | `Username=erp_worker;…` |
| `Database__AppRolePassword`, `Database__WorkerRolePassword` | used when the roles are first created |
| `Jwt__SigningKey` | ≥ 32 random bytes (`openssl rand -base64 48`) |
| `Security__FieldEncryptionKey`, `Security__BlindIndexKey` | `openssl rand -base64 32` each. **Back them up** — losing the encryption key makes QIDs/IBANs unreadable |
| `Platform__ApiKey` | long random string; protects tenant provisioning |
| `Email__Host`, `Email__Port`, `Email__Username`, `Email__Password`, `Email__FromAddress` | SMTP (Microsoft 365, Google Workspace, SES…) |
| `Cors__AllowedOrigins__0` | e.g. `https://app.yourdomain.qa` |

## 3. API container (Azure Container Apps, Qatar Central)

```bash
docker build -t <registry>/qatar-erp-api:<version> .
docker push <registry>/qatar-erp-api:<version>

# release step (Container Apps job or pipeline step), owner credentials:
docker run --rm -e Database__OwnerConnectionString=... -e Database__AppRolePassword=... \
  -e Database__WorkerRolePassword=... <image> --migrate-only

# then roll out the new revision with the runtime variables above; port 8080, health probe GET /health
```

Keep at least one replica running (`min replicas = 1`): background jobs (outbox emails, document-expiry reminders)
run inside the API process.

## 4. Web app (Cloudflare Pages)

- Repository: this repo · Root directory: `src/Erp.Web` · Build command: `npm ci && npx ng build` ·
  Output: `dist/erp-web/browser`
- After the first build, set the API address in `public/config.json` (`{ "apiBaseUrl": "https://api.yourdomain.qa" }`)
  or serve a different `config.json` per environment.
- `_redirects` sends deep links to `index.html`.

## 5. First customer

```bash
curl -X POST https://api.yourdomain.qa/api/platform/tenants -H "X-Platform-Key: <key>" -H "Content-Type: application/json" \
  --data-binary @tenant.json     # same body as scripts/demo.http step 1 (UTF-8 file for Arabic names)
```

Then sign in on the web app with the company code, complete **Payroll & WPS settings** and import employees from Excel.

## 6. Checklist before real payroll data

- [ ] SIF file validated with the customer's bank (WPS validator) — see the ⚠️ notes in the blueprint
- [ ] Payroll rules (overtime, gratuity basis, leave salary basis, sick bands) confirmed with a Qatari payroll adviser
- [ ] Field-encryption and JWT keys stored in Key Vault and backed up
- [ ] Database PITR restore tested once (restore to a scratch server, check the trial balance)
- [ ] SMTP sender domain has SPF/DKIM so reminders do not land in spam
