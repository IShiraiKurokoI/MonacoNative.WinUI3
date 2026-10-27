# Windows Win2D reference captures

The renderer probe draws a fixed sample containing Latin and CJK text, right-to-left text, emoji, selection colors, and a caret primitive. It runs at 96, 120, 144, and 192 DPI, writing both PNG captures and JSON fingerprints. This probe tests a small DirectWrite/Win2D surface, not the complete MonacoEditor control. UI and IME rendering are reviewed during desktop acceptance.

No reference images or fingerprints are generated in the repository. Each reference must come from a real Windows run and be reviewed before it is checked in.

1. On the designated Windows x64 test machine, run `./scripts/test-renderer-golden.ps1 -Configuration Release`.
2. Inspect the four PNG files at `artifacts/render-goldens/renderer-current-<dpi>dpi.png`. Record the Windows build, font configuration, graphics environment, and reviewer alongside the release acceptance evidence.
3. For each approved capture, copy `artifacts/render-goldens/renderer-current-<dpi>dpi.json` to `reference/render-goldens/windows-x64-<dpi>dpi.json`, substituting 96, 120, 144, and 192. Keep the fingerprints unchanged and commit all four approved files.
4. Run `./scripts/test-renderer-golden.ps1 -Configuration Release -RequireBaseline` to compare the geometry, sample metadata, ink counts, and complete pixel hash for every DPI.
5. Review and replace affected baselines when the renderer, operating system, fonts, or test environment changes intentionally.

Unattended runs capture the current images, and compare any approved references already present. Release mode requires all four reviewed references. Repeatability without an approved reference does not count as visual regression approval.
