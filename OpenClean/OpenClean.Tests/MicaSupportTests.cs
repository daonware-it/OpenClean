using System;
using OpenClean.Services.Theming;
using Xunit;

namespace OpenClean.Tests;

public sealed class MicaSupportTests
{
    [Fact]
    public void IsSupported_Entspricht_Build_22000_Grenze()
    {
        bool expected = Environment.OSVersion.Version.Build >= 22000;
        Assert.Equal(expected, MicaBackdrop.IsSupported);
    }

    [Fact]
    public void Apply_Auf_NullHandle_Wirft_Nicht()
    {
        // Ungültiges Handle -> DWM liefert nur einen Fehlercode, kein Throw.
        var ex = Record.Exception(() => MicaBackdrop.Apply(IntPtr.Zero, true));
        Assert.Null(ex);
    }
}
