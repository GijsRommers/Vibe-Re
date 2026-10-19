#!/usr/bin/env bash
set -euo pipefail
# The default Latchkey backend needs a real Secret Service, including on a
# signed-out server. This keyring belongs only to the disposable CI runner.
printf %s 'vibere-package-ci' | gnome-keyring-daemon --daemonize --unlock --components=secrets
exec xvfb-run -a dotnet run --project build/SmokeTests -c Release -- --root artifacts/stage/linux-x64
