# olaf sandbox: nuget (.NET)
# Toolchain: .NET 10 SDK. Manifests: *.csproj, packages.config, packages.lock.json.
# Smoke repo: https://github.com/xunit/xunit.git (nested csproj + packages.lock.json, recursive scan).
# Build from repo root: docker build -f sandbox/docker/nuget.Dockerfile -t olaf-sandbox-nuget .
ARG OLAF_VERSION=v0.1.2-preview.1
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && dotnet --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=nuget
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['nuget']['repo'])\") && /opt/olaf-verify/verify.sh --eco nuget --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
