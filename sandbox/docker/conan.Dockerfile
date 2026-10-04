# olaf sandbox: conan (C++ / Python)
# Toolchain: Python 3.13 + conan 2.x via pip.
# Manifests: conanfile.txt, conanfile.py, conan.lock.
# Smoke repo: https://github.com/conan-io/examples.git (nested consumer conanfile.txt files).
# Rejected: catchorg/Catch2 (root conanfile.py is a leaf recipe with no requires block -> olaf reports 0).
# Build from repo root: docker build -f sandbox/docker/conan.Dockerfile -t olaf-sandbox-conan .
ARG OLAF_VERSION=v0.1.3
FROM python:3.13-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq cmake \
  && rm -rf /var/lib/apt/lists/* \
  && pip install --no-cache-dir conan \
  && git --version && conan --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=conan
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['conan']['repo'])\") && /opt/olaf-verify/verify.sh --eco conan --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
