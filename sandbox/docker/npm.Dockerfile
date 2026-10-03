# olaf sandbox: npm (Node.js)
# Toolchain: Node 22 + npm. Manifests: package.json, package-lock.json, pnpm-lock.yaml, yarn.lock, bun.lock.
# Smoke repo: https://github.com/expressjs/express.git (package.json).
# Build from repo root: docker build -f sandbox/docker/npm.Dockerfile -t olaf-sandbox-npm .
ARG OLAF_VERSION=v0.1.3-preview.1
FROM node:22-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && node --version && npm --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=npm
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['npm']['repo'])\") && /opt/olaf-verify/verify.sh --eco npm --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
