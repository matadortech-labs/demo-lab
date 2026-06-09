#!/usr/bin/env bash
set -euo pipefail

WEB_ROOT="${WEB_ROOT:-/var/www/html}"
SUBPAGE="${SUBPAGE:-keyfactor}"
TARGET="$WEB_ROOT/$SUBPAGE"

install -d "$TARGET/assets"
install -m 0644 keyfactor/index.html "$TARGET/index.html"
install -m 0644 keyfactor/assets/styles.css "$TARGET/assets/styles.css"
install -m 0644 keyfactor/assets/cert-status.js "$TARGET/assets/cert-status.js"
install -m 0644 keyfactor/assets/keyfactor-command-lite.png "$TARGET/assets/keyfactor-command-lite.png"
install -m 0644 keyfactor/assets/keyfactor-logo-white.png "$TARGET/assets/keyfactor-logo-white.png"

if [ ! -x "$WEB_ROOT/cgi-bin/cert-info.cgi" ]; then
  echo "WARNING: $WEB_ROOT/cgi-bin/cert-info.cgi was not found or is not executable."
  echo "The page will install, but live certificate status requires your existing CGI endpoint."
fi

echo "Installed Keyfactor Command certificate status example to $TARGET"
echo "Open: https://<your-nginx-fqdn>/$SUBPAGE/"
