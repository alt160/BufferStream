# Changelog

All notable changes to this project are documented in this file.

## 1.0.2 - 2026-09-28

- Clarified the allocation and performance tradeoffs of repeated independent-object serialization.
- Documented the difference between per-item disposed buffers and one owned buffer reused with `Reset()`.
- Added guidance on unavoidable independent output arrays and on avoiding them when a synchronous consumer can use a borrowed written view.

## 1.0.0 - 2026-09-27

- Initial public release.
- Added pooled, reusable owned buffers plus fixed-capacity borrowed-memory roots.
- Added shallow cursor segments, borrowed-root rebinding, and direct read-only span and memory views.
- Added symmetric typed readers and writers for primitives, strings, 128-bit integers, identifiers, temporal values, arbitrary and complex numbers, numerics, geometry values, and packed bit data.
- Added symmetric collection reader/writer pairs with explicit `WithByteLength` and `WithCount` framing names.
- Added public signed ZigZag and unsigned base-128 7-bit codecs for 32-bit, 64-bit, and 128-bit integers.
- Added cursor-preserving positional writes for binary-header backpatching without changing stream position or length.
- Preserved `DateTime` kind and exact `DateTimeOffset` offset semantics during round trips.
- Added span-based stream overrides and direct memory writes that avoid temporary arrays on supported paths.
- Added bounds, disposal, malformed-input, truncated-frame, failure-atomicity, and reader/writer symmetry validation.
- Added Apache-2.0 licensing, SourceLink, symbol packages, deterministic builds, package validation, multi-runtime package-consumer smoke tests, continuous integration, and trusted release automation.
