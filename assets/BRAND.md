# Osoko identity

The open “o” is made from two opposing capture corners. Its diagonal openings
suggest a screenshot moving into and out of the shelf. Use the same mark for
the app, menu bar, and documentation.

- Ink: `#1a1c1a`; paper: `#f5f2eb`.
- Keep at least one stroke width of clear space around the mark.
- Use the ink artwork on light surfaces and the light artwork on dark surfaces.
- The wordmark is lowercase, tightly spaced system sans. SVG lettering is
  outlined, so it needs no installed font.
- `logo.svg` is the standalone mark; `app-icon.png` is the macOS tile.
- `packaging/AppIcon.icns` contains native resolutions from 16 through 1024 px.

The shared mark geometry lives in `Sources/OsokoKit/App/OsokoBrand.swift`.
Regenerate the checked-in artwork from the repository root on macOS:

```sh
swiftc Sources/OsokoKit/App/OsokoBrand.swift scripts/generate-brand-assets.swift -o /tmp/osoko-brand
/tmp/osoko-brand
```

This uses Apple's native graphics and font APIs and `iconutil`; no third-party
graphics packages are required. Normal app builds use the checked-in icon.
