# RFPEM Certificate Store Settings

Use these values when creating the RFPEM certificate store in Keyfactor Command.

| Field | Value |
|---|---|
| Category | RFPEM |
| Application | Optional, recommended: NGINX-Web |
| Client Machine | yourlinuxserver.domain.com |
| Store Path | /opt/keyfactor/nginx/staged/yourservername.domain.com.crt |
| Orchestrator | yourorchestrator |
| Set Server Username | yourlinuxuser |
| Set Server Password | Full SSH private key contents |
| Linux File Permissions on Store Creation | 644 |
| Linux File Owner on Store Creation | yourlinuxuser:yourlinuxuser |
| Sudo Impersonating User | root |
| Trust Store | false |
| Store Includes Chain | true |
| Separate Private Key File Location | /opt/keyfactor/nginx/staged/yourservername.domain.com.key |
| Ignore Private Key On Inventory | false |
| Remove Root Certificate From Chain | true |
| Include Port in SPN for WinRM | false |
| SSH Port | 22 |
| Use Shell Commands | true |
| Post Job Application Restart | NGINX Restart |
| Use SSL | false |
| Set Store Password | No Value / blank |
| Create Certificate Store If Missing | checked if available |
| Inventory Schedule | Immediate for first inventory; hourly recommended after validation |
