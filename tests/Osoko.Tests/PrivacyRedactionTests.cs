using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Osoko.Core.Services;

namespace Osoko.Tests;

/// <summary>
/// Sensitive-data patterns are private implementation details, so they are inspected
/// through reflection; the result record is tested through its public contract.
/// </summary>
public sealed class PrivacyRedactionTests
{
    private const int StripeIndex = 0;
    private const int SecretKeyIndex = 1;
    private const int GitHubIndex = 2;
    private const int AwsIndex = 3;
    private const int JwtIndex = 4;
    private const int BearerIndex = 5;
    private const int EmailIndex = 6;
    private const int CardIndex = 7;
    private const int PasswordUrlIndex = 8;

    [Theory]
    [MemberData(nameof(SensitiveSamples))]
    public void Sensitive_Patterns_Match_Expected_Category(int index, string sample, bool expected)
    {
        var patterns = GetSensitivePatterns();

        Assert.InRange(index, 0, patterns.Length - 1);

        if (expected)
        {
            Assert.Matches(patterns[index], sample);
        }
        else
        {
            Assert.DoesNotMatch(patterns[index], sample);
        }
    }

    [Fact]
    public void Patterns_Do_Not_Match_Ordinary_Text()
    {
        foreach (var pattern in GetSensitivePatterns())
        {
            Assert.DoesNotMatch(pattern, "Hello from Osoko");
            Assert.DoesNotMatch(pattern, "12345");
        }
    }

    [Fact]
    public void Patterns_Contain_All_Nine_Categories()
    {
        Assert.Equal(9, GetSensitivePatterns().Length);
    }

    [Fact]
    public void RedactionResult_Exposes_Redaction_State()
    {
        var bitmap = CreateBitmap(2, 2);
        var bytes = new byte[] { 1, 2, 3 };
        var result = new RedactionResult(bytes, bitmap, 3);

        Assert.True(result.HasRedactions);
        Assert.Equal(3, result.MatchCount);
        Assert.Same(bytes, result.PngBytes);
        Assert.Same(bitmap, result.RedactedImage);
    }

    [Fact]
    public void RedactionResult_Empty_Represents_No_Redactions()
    {
        var empty = RedactionResult.Empty;

        Assert.False(empty.HasRedactions);
        Assert.Equal(0, empty.MatchCount);
        Assert.Empty(empty.PngBytes);
        Assert.NotNull(empty.RedactedImage);
        Assert.Equal(1, empty.RedactedImage.PixelWidth);
        Assert.Equal(1, empty.RedactedImage.PixelHeight);
    }

    public static TheoryData<int, string, bool> SensitiveSamples => new()
    {
        { StripeIndex, "sk_live_abcdefghijklmnopqrstuvwx", true },
        { StripeIndex, "sk_live_short", false },

        { SecretKeyIndex, "sk-proj-abcdefghijklmnopqrstuvwxyz_0123456789", true },
        { SecretKeyIndex, "sk-short", false },

        { GitHubIndex, "ghp_" + new string('a', 36), true },
        { GitHubIndex, "ghp_short", false },

        { AwsIndex, "AKIAABCDEFGHIJKLMNOP", true },
        { AwsIndex, "AKIA123", false },

        { JwtIndex, "eyJhbGciOiJIUzI1.eyJzdWIiOiIxMjM0.SflKxwRJSMeK", true },
        { JwtIndex, "eyJ.eyJ.x", false },

        { BearerIndex, "Bearer abcdefghijklmnopqrstuvwxyz123456", true },
        { BearerIndex, "Bearer short", false },

        { EmailIndex, "alice@example.com", true },
        { EmailIndex, "not an email", false },

        { CardIndex, "4111 1111 1111 1111", true },
        { CardIndex, "1234 5678", false },

        { PasswordUrlIndex, "https://user:secret@example.com/path", true },
        { PasswordUrlIndex, "https://example.com/path", false },
    };

    private static Regex[] GetSensitivePatterns()
    {
        var field = typeof(PrivacyRedactionService).GetField(
            "SensitivePatterns",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PrivacyRedactionService.SensitivePatterns was not found.");

        return field.GetValue(null) as Regex[]
            ?? throw new InvalidOperationException("SensitivePatterns is not a Regex array.");
    }

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
