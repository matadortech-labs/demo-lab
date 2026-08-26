# Example Renewal Assurance Report

The production workflow renders this information as an HTML email. This public example intentionally uses fictional identifiers.

**Subject:** Application TLS Certificate Renewal Verified - Example NGINX Application

| Field | Example value |
|---|---|
| Outcome | PASS |
| Application | Example NGINX Application |
| Application owner | Example NGINX Application Team |
| Application URL tested | `https://nginx01.example.com/` |
| Previous certificate serial | `11223344556677889900AABBCCDDEEFF` |
| Renewed certificate serial | `FFEEDDCCBBAA00998877665544332211` |
| Certificate changed | Yes |
| Endpoint serving renewed certificate | PASS |
| Command inventory match | PASS |
| TLS handshake | PASS |
| Hostname/SAN match | PASS |
| Validation profile | `nginx-web-server-tls` |
| Correlation ID | `example-validation-correlation-id` |

The report is intended to provide application owners, PKI administrators, security reviewers, and auditors with evidence that renewal, deployment, and live endpoint validation completed as one controlled process.
