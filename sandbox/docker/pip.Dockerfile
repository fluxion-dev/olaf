# olaf sandbox: pip (Python)
# Toolchain: Python 3.13 + pip. Manifests: requirements.txt, pyproject.toml, poetry.lock, Pipfile.lock, uv.lock, environment.yml.
# Smoke repo: https://github.com/psf/requests.git (pyproject.toml).
# Build from repo root: docker build -f sandbox/docker/pip.Dockerfile -t olaf-sandbox-pip .
ARG OLAF_VERSION=v0.1.3-preview.1
FROM python:3.13-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && python3 --version && pip --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=pip
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['pip']['repo'])\") && /opt/olaf-verify/verify.sh --eco pip --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
