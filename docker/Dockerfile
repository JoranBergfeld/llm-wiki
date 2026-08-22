# The image ships the native-AOT binary CI already built for the release —
# it does not compile anything. The build context is assembled by the `docker`
# job in .github/workflows/ci.yml and must contain the extracted binaries at:
#
#   linux/amd64/wiki
#   linux/arm64/wiki
#
# which is exactly $TARGETPLATFORM/wiki for the two platforms we publish.
#
# Base image is ubuntu:24.04 on purpose: a native-AOT binary links against the
# builder's glibc, and CI builds on ubuntu-24.04 runners for both
# architectures. A slimmer base with an older glibc (debian:bookworm-slim) puts
# the binary in front of a libc it was not linked against, and it fails to
# start rather than failing to build.
FROM ubuntu:24.04

# ca-certificates so `wiki links check --external` can do TLS. Nothing else in
# the CLI reaches the network.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*

ARG TARGETPLATFORM
COPY ${TARGETPLATFORM}/wiki /usr/local/bin/wiki
RUN chmod +x /usr/local/bin/wiki

# Mount the vault here: `docker run -v /path/to/vault:/vault ghcr.io/...`.
# Writes land as the container user, so pass `--user $(id -u):$(id -g)` unless
# you want root-owned files in your vault.
ENV WIKI_VAULT=/vault
WORKDIR /vault
VOLUME ["/vault"]

ENTRYPOINT ["/usr/local/bin/wiki"]
CMD ["--help"]
