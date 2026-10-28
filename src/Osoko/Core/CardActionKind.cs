namespace Osoko.Core;

/// <summary>
/// The actions that can be bound to a screenshot card's action slots and click
/// gestures. Wire values match the macOS upstream (<c>copy_downscaled</c>,
/// <c>open_qr_link</c>, …) so settings imported from Osoko for macOS keep working.
/// </summary>
public enum CardActionKind
{
    /// <summary>No action is bound; the slot is hidden.</summary>
    None = 0,

    /// <summary>Copy a 1x (half resolution) copy of the capture to the clipboard.</summary>
    CopyDownscaled = 1,

    /// <summary>Copy the capture at its original resolution.</summary>
    CopyOriginal = 2,

    /// <summary>Run OCR and copy the recognised text.</summary>
    CopyOcr = 3,

    /// <summary>Black out API keys, tokens, emails and other sensitive text.</summary>
    RedactSensitive = 4,

    /// <summary>Copy the payload of a QR code / barcode found in the capture.</summary>
    CopyQrLink = 5,

    /// <summary>Open the payload of a QR code / barcode in the default browser.</summary>
    OpenQrLink = 6,

    /// <summary>Save directly to the folder configured in settings.</summary>
    SaveToFolder = 7,

    /// <summary>Prompt for a name and location before saving.</summary>
    SaveAs = 8,

    /// <summary>Keep the card on the shelf regardless of the stack limit.</summary>
    Pin = 9,

    /// <summary>Open the Windows share sheet for the capture.</summary>
    Share = 10,

    /// <summary>Open the capture in the default image application.</summary>
    OpenInPreview = 11,
}

/// <summary>
/// Display metadata and wire-format helpers for <see cref="CardActionKind"/>.
/// <para>
/// C# does not allow instance members on an enum, so the metadata is exposed as
/// extension methods. <see cref="ToWireValue"/> is the canonical, stable string
/// written to <c>settings.json</c>; everything else is presentation only.
/// </para>
/// </summary>
public static class CardActionKindExtensions
{
    private static readonly CardActionKind[] AllActions =
    [
        CardActionKind.CopyDownscaled,
        CardActionKind.CopyOriginal,
        CardActionKind.CopyOcr,
        CardActionKind.RedactSensitive,
        CardActionKind.CopyQrLink,
        CardActionKind.OpenQrLink,
        CardActionKind.SaveToFolder,
        CardActionKind.SaveAs,
        CardActionKind.Pin,
        CardActionKind.Share,
        CardActionKind.OpenInPreview,
        CardActionKind.None,
    ];

    private static readonly CardActionKind[] SelectableActions =
        AllActions.Where(action => action is not CardActionKind.None).ToArray();

    /// <summary>Every action, in the same order as the macOS upstream's <c>allCases</c>.</summary>
    public static IReadOnlyList<CardActionKind> All => AllActions;

    /// <summary>Alias for <see cref="All"/> that matches the common <c>Values</c> naming.</summary>
    public static IReadOnlyList<CardActionKind> Values => AllActions;

    /// <summary>Actions that can actually be bound to a slot (everything except <see cref="CardActionKind.None"/>).</summary>
    public static IReadOnlyList<CardActionKind> Selectable => SelectableActions;

    /// <summary>Maps an action to the stable string persisted in settings.</summary>
    public static string ToWireValue(this CardActionKind kind) => kind switch
    {
        CardActionKind.CopyDownscaled => "copy_downscaled",
        CardActionKind.CopyOriginal => "copy_original",
        CardActionKind.CopyOcr => "copy_ocr",
        CardActionKind.RedactSensitive => "redact_sensitive",
        CardActionKind.CopyQrLink => "copy_qr_link",
        CardActionKind.OpenQrLink => "open_qr_link",
        CardActionKind.SaveToFolder => "save_to_folder",
        CardActionKind.SaveAs => "save_as",
        CardActionKind.Pin => "pin",
        CardActionKind.Share => "share",
        CardActionKind.OpenInPreview => "open_preview",
        CardActionKind.None => "none",
        _ => "none",
    };

    /// <summary>Human readable label used by context menus and the settings window.</summary>
    public static string DisplayName(this CardActionKind kind) => kind switch
    {
        CardActionKind.CopyDownscaled => "Copy 1x Downscaled",
        CardActionKind.CopyOriginal => "Copy 2x Original",
        CardActionKind.CopyOcr => "Copy Text (OCR)",
        CardActionKind.RedactSensitive => "Redact Sensitive Data",
        CardActionKind.CopyQrLink => "Copy QR Code Link",
        CardActionKind.OpenQrLink => "Open QR Link",
        CardActionKind.SaveToFolder => "Save to Location",
        CardActionKind.SaveAs => "Save As...",
        CardActionKind.Pin => "Pin to Stack",
        CardActionKind.Share => "Share...",
        CardActionKind.OpenInPreview => "Open in Preview",
        CardActionKind.None => "None (Hidden)",
        _ => kind.ToString(),
    };

    /// <summary>Segoe Fluent Icons glyph for the action.</summary>
    public static string Glyph(this CardActionKind kind) => kind switch
    {
        CardActionKind.CopyDownscaled => "\uE8C8", // Copy
        CardActionKind.CopyOriginal => "\uE8A5", // Document
        CardActionKind.CopyOcr => "\uE8D2", // Font (recognised text)
        CardActionKind.RedactSensitive => "\uE72E", // Lock
        CardActionKind.CopyQrLink => "\uE71B", // Link
        CardActionKind.OpenQrLink => "\uE8A7", // Open in new window
        CardActionKind.SaveToFolder => "\uE8B7", // Folder
        CardActionKind.SaveAs => "\uE74E", // Save
        CardActionKind.Pin => "\uE718", // Pin
        CardActionKind.Share => "\uE72D", // Share
        CardActionKind.OpenInPreview => "\uE890", // View
        CardActionKind.None => "\uE738", // Remove (hidden slot)
        _ => string.Empty,
    };

    /// <summary>Tooltip shown when the pointer rests on the action button.</summary>
    public static string Tooltip(this CardActionKind kind) => kind switch
    {
        CardActionKind.CopyDownscaled => "Copy 1x logical resolution to clipboard",
        CardActionKind.CopyOriginal => "Copy original 2x capture to clipboard",
        CardActionKind.CopyOcr => "Extract and copy text",
        CardActionKind.RedactSensitive => "Redact API keys, tokens, and emails with solid black bars",
        CardActionKind.CopyQrLink => "Copy URL or text from detected QR code",
        CardActionKind.OpenQrLink => "Open QR link in default browser",
        CardActionKind.SaveToFolder => "Save directly to configured folder",
        CardActionKind.SaveAs => "Prompt to rename and save",
        CardActionKind.Pin => "Keep card in stack",
        CardActionKind.Share => "Open the Windows share sheet",
        CardActionKind.OpenInPreview => "Open in the default image app",
        CardActionKind.None => "Hide this action slot",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Reads a persisted action. Accepts wire values (<c>open_qr_link</c>) as well as
    /// enum member names (<c>OpenQrLink</c>) so hand-edited settings do not break.
    /// Unknown or empty values fall back to <see cref="CardActionKind.None"/>.
    /// </summary>
    public static bool TryParseWireValue(string? wireValue, out CardActionKind kind)
    {
        if (!string.IsNullOrWhiteSpace(wireValue))
        {
            var candidate = wireValue.Trim();
            foreach (var action in AllActions)
            {
                if (string.Equals(action.ToWireValue(), candidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(action.ToString(), candidate, StringComparison.OrdinalIgnoreCase))
                {
                    kind = action;
                    return true;
                }
            }
        }

        kind = CardActionKind.None;
        return false;
    }

    /// <summary>Parses a persisted action, throwing when <paramref name="wireValue"/> is unknown.</summary>
    /// <exception cref="ArgumentException">The value is not a known action.</exception>
    public static CardActionKind ParseWireValue(string wireValue)
    {
        if (TryParseWireValue(wireValue, out var kind))
        {
            return kind;
        }

        throw new ArgumentException($"'{wireValue}' is not a known card action.", nameof(wireValue));
    }
}
