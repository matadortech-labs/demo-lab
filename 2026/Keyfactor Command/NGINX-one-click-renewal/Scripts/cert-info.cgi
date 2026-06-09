#!/usr/bin/env bash
set -euo pipefail

CERT_FILE="/etc/nginx/certs/yourservername.domain.com.crt"

json_escape() {
  sed 's/\\/\\\\/g; s/"/\\"/g'
}

printf 'Content-Type: application/json\r\n'
printf 'Cache-Control: no-store\r\n'
printf '\r\n'

if [ ! -r "$CERT_FILE" ]; then
  printf '{"error":"certificate file is not readable: %s"}\n' "$CERT_FILE" | json_escape
  exit 0
fi

serial_raw="$(openssl x509 -in "$CERT_FILE" -noout -serial | sed 's/^serial=//')"
not_before="$(openssl x509 -in "$CERT_FILE" -noout -startdate | sed 's/^notBefore=//')"
not_after="$(openssl x509 -in "$CERT_FILE" -noout -enddate | sed 's/^notAfter=//')"

# Display serial as colon-delimited hex for readability.
serial_colon="$(echo "$serial_raw" | sed 's/../&:/g; s/:$//')"

printf '{\n'
printf '  "serialNumber": "%s",\n' "$serial_colon"
printf '  "notBefore": "%s",\n' "$not_before"
printf '  "notAfter": "%s"\n' "$not_after"
printf '}\n'
