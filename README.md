# llm-wiki

[![CI](https://github.com/JoranBergfeld/llm-wiki/actions/workflows/ci.yml/badge.svg)](https://github.com/JoranBergfeld/llm-wiki/actions/workflows/ci.yml)

LLM Wiki implementation with a CLI to add more deterministic controls.

Every push to `main` runs the test suite and, if it passes, builds the native-AOT
`wiki` binary for linux-x64, linux-arm64, win-x64, osx-x64, and osx-arm64. The
latest binaries are attached to the rolling [`latest`](https://github.com/JoranBergfeld/llm-wiki/releases/tag/latest)
prerelease.
