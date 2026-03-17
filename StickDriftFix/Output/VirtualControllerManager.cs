using System.Numerics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DriftCore.Output;

/// <summary>
/// Manages the ViGEm virtual Xbox 360 controller.
///
/// Prerequisites:
///   • ViGEmBus driver must be installed:
///     https://github.com/ViGEm/ViGEmBus/releases
///
/// The virtual device is what games see.  The physical controller is
/// optionally hidden from games via HidHide (separate install).
/// </summary>
public class VirtualControllerManager : IDisposable
{
    private ViGEmClient?       _client;
    private IXbox360Controller? _controller;
    private bool               _disposed;

    public bool   IsConnected { get; private set; }
    public string LastError   { get; private set; } = string.Empty;

    // ── Lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialise the ViGEmClient and create + connect a virtual Xbox 360 controller.
    /// Returns false and populates LastError if ViGEmBus is not installed.
    /// </summary>
    public bool Connect()
    {
        try
        {
            _client     = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();

            // CRITICAL: disable auto-submit so individual Set* calls don't each
            // fire a report. We batch everything and call SubmitReport() once per frame.
            _controller.AutoSubmitReport = false;

            _controller.Connect();
            IsConnected = true;
            LastError   = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            LastError   = ex.Message;
            return false;
        }
    }

    public void Disconnect()
    {
        try
        {
            _controller?.Disconnect();
        }
        catch { /* intentionally swallowed */ }
        finally
        {
            IsConnected = false;
        }
    }

    // ── State submission ─────────────────────────────────────────────────

    /// <summary>
    /// Push a complete controller state to the virtual device.
    /// All values are normalised; conversion to raw ranges happens here.
    /// </summary>
    /// <param name="leftStick">  Corrected left  stick [-1, 1].</param>
    /// <param name="rightStick"> Corrected right stick [-1, 1].</param>
    /// <param name="leftTrigger"> Left  trigger [0, 1].</param>
    /// <param name="rightTrigger">Right trigger [0, 1].</param>
    /// <param name="buttons">    Raw XInput button bitmask (pass-through).</param>
    public void SendState(
        Vector2 leftStick,
        Vector2 rightStick,
        float   leftTrigger,
        float   rightTrigger,
        ushort  buttons)
    {
        if (!IsConnected || _controller is null) return;

        try
        {
            // Clear the entire report struct in one call —
            // resets all axes, sliders, and buttons to zero.
            _controller.ResetReport();

            // Axes — normalised [-1,1] → signed 16-bit
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX,
                (short)Math.Clamp(leftStick.X  * 32767f, -32768f, 32767f));
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY,
                (short)Math.Clamp(leftStick.Y  * 32767f, -32768f, 32767f));
            _controller.SetAxisValue(Xbox360Axis.RightThumbX,
                (short)Math.Clamp(rightStick.X * 32767f, -32768f, 32767f));
            _controller.SetAxisValue(Xbox360Axis.RightThumbY,
                (short)Math.Clamp(rightStick.Y * 32767f, -32768f, 32767f));

            // Triggers — normalised [0,1] → unsigned 8-bit
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger,
                (byte)Math.Clamp(leftTrigger  * 255f, 0f, 255f));
            _controller.SetSliderValue(Xbox360Slider.RightTrigger,
                (byte)Math.Clamp(rightTrigger * 255f, 0f, 255f));

            // Buttons — only set the ones that are active (ResetReport cleared the rest)
            SetActiveButtons(buttons);

            // Single submit for the entire frame — efficient at 60 Hz
            _controller.SubmitReport();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void SetActiveButtons(ushort raw)
    {
        if (_controller is null) return;
        if ((raw & 0x0001) != 0) _controller.SetButtonState(Xbox360Button.Up,            true);
        if ((raw & 0x0002) != 0) _controller.SetButtonState(Xbox360Button.Down,          true);
        if ((raw & 0x0004) != 0) _controller.SetButtonState(Xbox360Button.Left,          true);
        if ((raw & 0x0008) != 0) _controller.SetButtonState(Xbox360Button.Right,         true);
        if ((raw & 0x0010) != 0) _controller.SetButtonState(Xbox360Button.Start,         true);
        if ((raw & 0x0020) != 0) _controller.SetButtonState(Xbox360Button.Back,          true);
        if ((raw & 0x0040) != 0) _controller.SetButtonState(Xbox360Button.LeftThumb,     true);
        if ((raw & 0x0080) != 0) _controller.SetButtonState(Xbox360Button.RightThumb,    true);
        if ((raw & 0x0100) != 0) _controller.SetButtonState(Xbox360Button.LeftShoulder,  true);
        if ((raw & 0x0200) != 0) _controller.SetButtonState(Xbox360Button.RightShoulder, true);
        if ((raw & 0x1000) != 0) _controller.SetButtonState(Xbox360Button.A,             true);
        if ((raw & 0x2000) != 0) _controller.SetButtonState(Xbox360Button.B,             true);
        if ((raw & 0x4000) != 0) _controller.SetButtonState(Xbox360Button.X,             true);
        if ((raw & 0x8000) != 0) _controller.SetButtonState(Xbox360Button.Y,             true);
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            Disconnect();
            _client?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
