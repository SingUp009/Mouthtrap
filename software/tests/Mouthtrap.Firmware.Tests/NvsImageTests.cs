using System.Security.Cryptography;
using Mouthtrap.Firmware;
using Mouthtrap.Tests;

namespace Mouthtrap.Firmware.Tests;

public sealed class NvsImageTests
{
    // The SHA-256 of what ESP-IDF's nvs_partition_gen 0.1.9 (the version ESP-IDF 5.5.4 uses, as provision.py runs it)
    // writes for the same settings into a 0x6000-byte partition.
    [Theory]
    [InlineData("home", "password123", "0123456789abcdef0123456789abcdef",
        "dbdbca8eeb562c56a7f1b598d54bf4da71619155651c705ba5d62f1ac6ae688c")]
    [InlineData("あいうえおかきくけこxy", "ppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppp",
        "TTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTTT",
        "f665fb52b7e044df20502599f36ad8db5763407a751f640758eb064f91b72e90")]
    public void Create_Settings_MatchesTheEspIdfGenerator(string ssid, string password, string token, string sha256)
    {
        // Given
        var settings = ResultAssert.Ok(WifiSettings.Create(ssid, password, token));

        // When
        var image = NvsImage.Create(settings);

        // Then
        Assert.Equal(NvsImage.Size, image.Length);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(image)));
    }

    [Fact]
    public void Create_AnySettings_LeavesEveryPageButTheFirstErased()
    {
        // Given
        var settings = ResultAssert.Ok(WifiSettings.Create("home", "password123", StreamToken.Generate()));

        // When
        var image = NvsImage.Create(settings);

        // Then
        Assert.All(image.AsSpan(4096).ToArray(), value => Assert.Equal(0xff, value));
    }
}
