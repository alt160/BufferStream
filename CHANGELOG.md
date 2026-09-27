# Changelog

All notable changes to this project are documented in this file.

## 1.0.0 - 2026-09-27

- Initial public release.
- Added symmetric collection reader/writer pairs with explicit `WithByteLength` and `WithCount` framing names.
- Added malformed and truncated collection-frame validation.
- Added `ReadBoolean` and `ReadHalf` scalar counterparts, documented direct-write framing, and guarded capacity-based writes after disposal.
