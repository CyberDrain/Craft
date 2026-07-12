#!/bin/sh
# Emit KEY=VALUE lines describing the deployed frontend bundle, so the harness
# targets the REAL (content-hashed) filenames of whatever image is under test.
cd /app/Frontend 2>/dev/null || { echo "ERR=no_frontend"; exit 0; }

APP=$(find _next/static/chunks/pages -name '_app-*.js' 2>/dev/null | head -1)
[ -z "$APP" ] && APP=$(find _next/static/chunks -type f -name '*.js' -printf '%s %p\n' 2>/dev/null | sort -rn | head -1 | sed 's/^[0-9]* //')
CSS=$(find _next/static/css -type f -name '*.css' 2>/dev/null | head -1)
IMG="reportImages/city.jpg"; [ -f "$IMG" ] || IMG="logo.png"

echo "APPJS=/$APP"
if [ -n "$APP" ]; then echo "APPJS_SIZE=$(stat -c %s "$APP")"; else echo "APPJS_SIZE=0"; fi
if [ -n "$APP" ] && [ -f "$APP.br" ]; then echo "APPJS_BR=1"; else echo "APPJS_BR=0"; fi
echo "CSS=/$CSS"
if [ -n "$CSS" ]; then echo "CSS_SIZE=$(stat -c %s "$CSS")"; else echo "CSS_SIZE=0"; fi
echo "IMG=/$IMG"
echo "FRONTEND_BYTES=$(du -sb . | cut -f1)"
echo "BR_COUNT=$(find . -name '*.br' | wc -l)"
echo "GZ_COUNT=$(find . -name '*.gz' | wc -l)"

# Extra targets for a richer asset mix: 2nd-largest JS chunk + largest non-index route HTML page.
CHUNK2=$(find _next/static/chunks -type f -name '*.js' -printf '%s %p\n' 2>/dev/null | sort -rn | sed -n '2p' | sed 's/^[0-9]* //')
ROUTEHTML=$(find . -maxdepth 1 -type f -name '*.html' ! -name 'index.html' -printf '%s %f\n' 2>/dev/null | sort -rn | head -1 | sed 's/^[0-9]* //')
echo "CHUNK2=/$CHUNK2"
echo "ROUTEHTML=/$ROUTEHTML"
