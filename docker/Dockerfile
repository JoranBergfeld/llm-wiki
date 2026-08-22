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

# The CA bundle is architecture-independent data, so it is fetched once on the
# build machine's own platform and copied in. Doing it as a RUN in the target
# stage instead would mean apt-get executing under QEMU for the arm64 image:
# minutes of emulation, and a dependency on binfmt being registered, to
# produce bytes that are identical either way.
FROM --platform=$BUILDPLATFORM ubuntu:24.04 AS certs
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*

FROM ubuntu:24.04
# ca-certificates so `wiki links check --external` can do TLS. Nothing else in
# the CLI reaches the network.
COPY --from=certs /usr/share/ca-certificates /usr/share/ca-certificates
COPY --from=certs /etc/ssl/certs /etc/ssl/certs

# No RUN in this stage, by design: with the binary copied in pre-built and
# pre-chmodded, nothing in the target image is ever executed at build time, so
# the arm64 image builds on an amd64 runner with no emulation at all.
ARG TARGETPLATFORM
COPY --chmod=0755 ${TARGETPLATFORM}/wiki /usr/local/bin/wiki

# Mount the vault here: `docker run -v /path/to/vault:/vault ghcr.io/...`.
# Writes land as the container user, so pass `--user $(id -u):$(id -g)` unless
# you want root-owned files in your vault.
ENV WIKI_VAULT=/vault
WORKDIR /vault
VOLUME ["/vault"]

ENTRYPOINT ["/usr/local/bin/wiki"]
CMD ["--help"]
