# olaf sandbox: cargo (Rust)
# Toolchain: Rust stable + cargo. Manifests: Cargo.toml, Cargo.lock.
# Smoke repo: https://github.com/BurntSushi/ripgrep.git (Cargo.toml + Cargo.lock).
# Build from repo root: docker build -f sandbox/docker/cargo.Dockerfile -t olaf-sandbox-cargo .
ARG OLAF_VERSION=v0.1.2-preview.1
FROM rust:1-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && cargo --version && rustc --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=cargo
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['cargo']['repo'])\") && /opt/olaf-verify/verify.sh --eco cargo --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
