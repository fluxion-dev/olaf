# olaf sandbox: swift
# Toolchain: Swift 6.1. Manifests: Package.swift, Package.resolved.
# Smoke repo: https://github.com/vapor/vapor.git (Package.swift with ~24 .package deps).
# Rejected: apple/swift-argument-parser (leaf package, zero .package deps -> olaf reports 0).
# Build from repo root: docker build -f sandbox/docker/swift.Dockerfile -t olaf-sandbox-swift .
ARG OLAF_VERSION=v0.1.3
FROM swift:6.1-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && swift --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=swift
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['swift']['repo'])\") && /opt/olaf-verify/verify.sh --eco swift --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
