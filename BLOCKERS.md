# Blockers

Exact asks for the owner. Claude Code appends here; the owner clears items and replies "done".

- [ ] Sending subdomain chosen (e.g. `news.<yourdomain>`) and DNS access confirmed
- [ ] ACS Email quota request filed (Azure portal → Help + support → Service and subscription limits → "Azure Communication Services Email: Sending Limits"; ask for 30/min, 2,000/hour, 10,000/day; consented newsletter subscribers, ~10k contacts, 1–2 sends/month)
- [ ] `az login` done in this environment, or Azure resources created from `infra/main.bicep` by the owner
- [ ] Entra app registration created; client ID and secret available
- [ ] GitHub repo secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and variable `AZURE_WEBAPP_NAME` set
- [ ] Seed inboxes on Outlook, Gmail, Yahoo, iCloud
- [ ] Subscriber CSV available (email column required, everything else optional)
