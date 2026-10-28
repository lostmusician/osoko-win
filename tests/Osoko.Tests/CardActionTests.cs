using System.Text.Json;
using Osoko.Core;

namespace Osoko.Tests;

/// <summary>
/// The card action enum is part of the persisted settings contract and also drives the
/// toolbar/context menu labels, so its ordering, wire values and metadata are covered
/// explicitly.
/// </summary>
public sealed class CardActionTests
{
    private static readonly CardActionKind[] ExpectedOrder =
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

    private static readonly (CardActionKind Action, string WireValue)[] ExpectedWireValues =
    [
        (CardActionKind.CopyDownscaled, "copy_downscaled"),
        (CardActionKind.CopyOriginal, "copy_original"),
        (CardActionKind.CopyOcr, "copy_ocr"),
        (CardActionKind.RedactSensitive, "redact_sensitive"),
        (CardActionKind.CopyQrLink, "copy_qr_link"),
        (CardActionKind.OpenQrLink, "open_qr_link"),
        (CardActionKind.SaveToFolder, "save_to_folder"),
        (CardActionKind.SaveAs, "save_as"),
        (CardActionKind.Pin, "pin"),
        (CardActionKind.Share, "share"),
        (CardActionKind.OpenInPreview, "open_preview"),
        (CardActionKind.None, "none"),
    ];

    [Fact]
    public void All_And_Values_Expose_Every_Action_In_Upstream_Order()
    {
        Assert.Equal(ExpectedOrder, CardActionKindExtensions.All.ToArray());
        Assert.Equal(ExpectedOrder, CardActionKindExtensions.Values.ToArray());
        Assert.Equal(12, Enum.GetValues<CardActionKind>().Length);
        Assert.Equal(12, CardActionKindExtensions.All.Count);
        Assert.Equal(11, CardActionKindExtensions.Selectable.Count);
        Assert.DoesNotContain(CardActionKind.None, CardActionKindExtensions.Selectable);
    }

    [Theory]
    [MemberData(nameof(WireValueCases))]
    public void ToWireValue_Uses_Stable_Snake_Case_Strings(CardActionKind action, string expected)
    {
        Assert.Equal(expected, action.ToWireValue());
    }

    [Fact]
    public void TryParseWireValue_Reads_Wire_And_Enum_Names_Case_Insensitively()
    {
        foreach (var action in ExpectedOrder)
        {
            Assert.True(CardActionKindExtensions.TryParseWireValue(action.ToWireValue(), out var fromWire));
            Assert.Equal(action, fromWire);

            Assert.True(CardActionKindExtensions.TryParseWireValue(action.ToString(), out var fromName));
            Assert.Equal(action, fromName);
        }

        Assert.True(CardActionKindExtensions.TryParseWireValue("COPY_OCR", out var upperWire));
        Assert.Equal(CardActionKind.CopyOcr, upperWire);

        Assert.True(CardActionKindExtensions.TryParseWireValue(" openqrlink ", out var enumName));
        Assert.Equal(CardActionKind.OpenQrLink, enumName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-action")]
    [InlineData("copy_ocr_extra")]
    public void TryParseWireValue_Rejects_Invalid_Values_And_Returns_None(string? value)
    {
        Assert.False(CardActionKindExtensions.TryParseWireValue(value, out var parsed));
        Assert.Equal(CardActionKind.None, parsed);
    }

    [Fact]
    public void ParseWireValue_Throws_For_Unknown_Action()
    {
        Assert.Throws<ArgumentException>(() => CardActionKindExtensions.ParseWireValue("not-a-real-action"));
    }

    [Fact]
    public void Metadata_Is_Not_Empty_For_Any_Action()
    {
        foreach (var action in ExpectedOrder)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.DisplayName()));
            Assert.False(string.IsNullOrWhiteSpace(action.Glyph()));
            Assert.False(string.IsNullOrWhiteSpace(action.Tooltip()));
        }
    }

    [Fact]
    public void Json_Round_Trips_Every_Action_As_Its_Wire_String()
    {
        foreach (var (action, wireValue) in ExpectedWireValues)
        {
            var json = JsonSerializer.Serialize(action);

            Assert.Equal($"\"{wireValue}\"", json);
            Assert.Equal(action, JsonSerializer.Deserialize<CardActionKind>(json));
        }
    }

    [Fact]
    public void Json_Reads_Wire_Strings_And_Enum_Names()
    {
        Assert.Equal(CardActionKind.CopyOcr, JsonSerializer.Deserialize<CardActionKind>("\"copy_ocr\""));
        Assert.Equal(CardActionKind.OpenQrLink, JsonSerializer.Deserialize<CardActionKind>("\"OpenQrLink\""));
    }

    public static TheoryData<CardActionKind, string> WireValueCases
    {
        get
        {
            var data = new TheoryData<CardActionKind, string>();
            foreach (var (action, wireValue) in ExpectedWireValues)
            {
                data.Add(action, wireValue);
            }

            return data;
        }
    }
}
