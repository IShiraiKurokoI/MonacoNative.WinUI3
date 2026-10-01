# Windows renderer golden image workflow

`tests/RendererGoldenTests` renders a fixed globalization corpus through Win2D/DirectWrite on a forced software `CanvasDevice` at 96 DPI. The normal Windows CI gate performs two renders in one process, requires a non-empty image, requires bit-for-bit determinism, and uploads the resulting JSON pixel fingerprint.

A checked-in baseline is intentionally **not fabricated on non-Windows hosts**. To freeze a Windows image after reviewing it, run the probe on the supported Windows image, copy the generated JSON here, and pass it back with `--baseline <file>`. The harness then becomes an exact golden comparison. If the CI image or system fonts are intentionally upgraded, record and review a new baseline rather than silently accepting the change.

The corpus covers ASCII source code, combining marks, CJK, Hangul, emoji/ZWJ, Hebrew/Arabic mixed-direction text and editor punctuation.
