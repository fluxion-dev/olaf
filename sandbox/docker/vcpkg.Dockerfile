# olaf sandbox: vcpkg (C++)
# Toolchain: Ubuntu build tools (git, cmake, ninja) for vcpkg-style C++ work.
# olaf parses vcpkg.json statically; a full vcpkg bootstrap is intentionally NOT
# baked in (keeps the image small and the test hermetic/offline).
# Manifests: vcpkg.json.
# Smoke repo: https://github.com/microsoft/terminal.git (root vcpkg.json).
# Build from repo root: docker build -f sandbox/docker/vcpkg.Dockerfile -t olaf-sandbox-vcpkg .
ARG OLAF_VERSION=v0.1.1-preview.1
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends \
    git curl ca-certificates jq python3 cmake ninja-build tar unzip pkg-config \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && cmake --version | head -n 1

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=vcpkg
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['vcpkg']['repo'])\") && /opt/olaf-verify/verify.sh --eco vcpkg --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
