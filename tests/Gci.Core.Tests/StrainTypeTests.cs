using Gci.Core.Services;
using Xunit;

namespace Gci.Core.Tests;

public class StrainTypeTests
{
    [Theory]
    [InlineData("Indica", "Indica")]
    [InlineData("SATIVA", "Sativa")]
    [InlineData("Hybrid", "Hybrid")]
    [InlineData("Sativa-Hybrid", "Hybrid")]
    [InlineData("Indica-Hybrid", "Hybrid")]
    [InlineData("Ratio", "CBD / ratio")]
    [InlineData("Cbd", "CBD / ratio")]
    [InlineData("CBN", "CBD / ratio")]
    [InlineData("1 to 1", "CBD / ratio")]
    [InlineData("1:1", "CBD / ratio")]
    [InlineData("THC", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Groups_menu_strain_labels_like_the_web_app(string? label, string? expected) =>
        Assert.Equal(expected, StrainType.Of(label));
}
