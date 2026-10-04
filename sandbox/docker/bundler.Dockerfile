# olaf sandbox: bundler (Ruby)
# Toolchain: Ruby 3.4 + bundler. Manifests: Gemfile, Gemfile.lock, *.gemspec.
# Smoke repo: https://github.com/jekyll/jekyll.git (Gemfile + jekyll.gemspec).
# Build from repo root: docker build -f sandbox/docker/bundler.Dockerfile -t olaf-sandbox-bundler .
ARG OLAF_VERSION=v0.1.3
FROM ruby:3.4-bookworm

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
  && apt-get install -y --no-install-recommends git curl ca-certificates jq python3 \
  && rm -rf /var/lib/apt/lists/* \
  && git --version && ruby --version && (bundle --version || gem install bundler)

ARG OLAF_VERSION
RUN curl -fSL -o /usr/local/bin/olaf \
    "https://github.com/fluxion-dev/olaf/releases/download/${OLAF_VERSION}/olaf-linux-x64" \
  && chmod +x /usr/local/bin/olaf \
  && olaf --version

WORKDIR /work
COPY sandbox/common/verify.sh sandbox/common/validate.py /opt/olaf-verify/
COPY sandbox/repos.json /opt/olaf-verify/repos.json
RUN chmod +x /opt/olaf-verify/verify.sh

ENV ECO=bundler
CMD ["sh", "-c", "REPO=$(python3 -c \"import json;print(json.load(open('/opt/olaf-verify/repos.json'))['ecosystems']['bundler']['repo'])\") && /opt/olaf-verify/verify.sh --eco bundler --repo \"$REPO\" --min-packages 1 --workdir /work --online --formats all"]
