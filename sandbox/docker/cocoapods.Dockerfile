# olaf sandbox: cocoapods (Ruby + CocoaPods)
# Toolchain: Ruby 3.4 + cocoapods gem. Manifests: Podfile, Podfile.lock.
# NOTE: olaf does NOT parse *.podspec, only Podfile/Podfile.lock.
# Smoke repo: https://github.com/artsy/eidolon.git (root Podfile + Podfile.lock).
# Rejected: Alamofire/Alamofire (only Alamofire.podspec, no Podfile).
# Build from repo root: docker build -f sandbox/docker/cocoapods.Dockerfile -t olaf-sandbox-cocoapods .
ARG OLAF_VERSION=v0.1.1-preview.1
FROM ruby:3.4-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && gem install cocoapods --no-document \
  && git --version && ruby --version && pod --version

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=cocoapods
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['cocoapods']['repo'])\") && /opt/olaf-verify/verify.sh --eco cocoapods --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
