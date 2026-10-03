# olaf sandbox: gradle (Java)
# Toolchain: Gradle 8 + JDK 21. Manifests: build.gradle, build.gradle.kts, libs.versions.toml.
# Smoke repo: https://github.com/mockito/mockito.git (Kotlin DSL + version catalog).
# Build from repo root: docker build -f sandbox/docker/gradle.Dockerfile -t olaf-sandbox-gradle .
ARG OLAF_VERSION=v0.1.3-preview.1
FROM gradle:8-jdk21

ENV DEBIAN_FRONTEND=noninteractive
USER root
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && gradle --version && python3 --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=gradle
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['gradle']['repo'])\") && /opt/olaf-verify/verify.sh --eco gradle --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
