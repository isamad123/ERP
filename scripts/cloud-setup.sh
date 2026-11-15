#!/usr/bin/env bash
# Setup script for the Claude Code cloud environment (paste into the environment's "Setup script" box).
# Installs the .NET 10 SDK. PostgreSQL 16, Docker and Node.js are pre-installed in the cloud image.
set -euo pipefail
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^10\.'; then exit 0; fi
export DEBIAN_FRONTEND=noninteractive
sudo apt-get update -y || true
# 1) Ubuntu archive or backports
if ! sudo apt-get install -y dotnet-sdk-10.0; then
  sudo apt-get install -y software-properties-common || true
  sudo add-apt-repository -y ppa:dotnet/backports || true
  sudo apt-get update -y || true
  sudo apt-get install -y dotnet-sdk-10.0 || {
    # 2) Microsoft package repository
    . /etc/os-release
    curl -fsSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/ms.deb
    sudo dpkg -i /tmp/ms.deb && sudo apt-get update -y && sudo apt-get install -y dotnet-sdk-10.0
  }
fi
dotnet --list-sdks
# Fallback if all of the above fail: build inside the container image mcr.microsoft.com/dotnet/sdk:10.0
