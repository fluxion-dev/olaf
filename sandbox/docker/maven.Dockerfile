# olaf sandbox: maven (Java)
# Toolchain: Maven 3 + Temurin JDK 21. Manifests: pom.xml.
# Smoke repo: https://github.com/apache/commons-lang.git (root pom.xml).
# Build from repo root: docker build -f sandbox/docker/maven.Dockerfile -t olaf-sandbox-maven .
ARG OLAF_VERSION=v0.1.2-preview.1
FROM maven:3-eclipse-temurin-21

ENV DEBIAN_FRONTEND=noninteractive
USER root
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && mvn --version && python3 --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=maven
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['maven']['repo'])\") && /opt/olaf-verify/verify.sh --eco maven --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
