using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Pointer lock on the native window. The grab itself needs a window and an
/// event loop, so what is checked here is the request and the contract around
/// it; the grab is exercised by running a window.
/// </summary>
[Collection("PointerLock")]
public class PointerLockTests : IDisposable
{
    public void Dispose() => AppWindow.PointerLocked = false;

    [Fact]
    public void TheRequestSurvivesARoundTrip()
    {
        Assert.False(AppWindow.PointerLocked);

        AppWindow.PointerLocked = true;
        Assert.True(AppWindow.PointerLocked);

        AppWindow.PointerLocked = false;
        Assert.False(AppWindow.PointerLocked);
    }

    [Fact]
    public void AskingTwiceIsNotAnError()
    {
        // The event loop applies the request when it next runs, so setting it
        // repeatedly before a window exists has to be harmless.
        AppWindow.PointerLocked = true;
        AppWindow.PointerLocked = true;
        Assert.True(AppWindow.PointerLocked);
    }

    [Fact]
    public void RawMouseMoveHasItsOwnEventKind()
    {
        // The value is mirrored by the Rust enum and must stay put: a shifted
        // event kind would deliver mouse movement as something else entirely.
        Assert.Equal(13, (int)InputEventKind.RawMouseMove);
        Assert.Equal(12, (int)InputEventKind.ScaleFactorChanged);
    }
}
