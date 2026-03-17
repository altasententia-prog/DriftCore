using System.Numerics;
using System.Runtime.InteropServices;

namespace DriftCore.Input;

// ═══════════════════════════════════════════════════════════════════════════
// XInput native structures
// ═══════════════════════════════════════════════════════════════════════════

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public ushort Buttons;
    public byte   LeftTrigger;
    public byte   RightTrigger;
    public short  ThumbLX;
    public short  ThumbLY;
    public short  ThumbRX;
    public short  ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint         PacketNumber;
    public XInputGamepad Gamepad;
}

// XInput button bitmask constants (matches XINPUT_GAMEPAD_* defines)
[Flags]
public enum XInputButton : ushort
{
    DPadUp        = 0x0001,
    DPadDown      = 0x0002,
    DPadLeft      = 0x0004,
    DPadRight     = 0x0008,
    Start         = 0x0010,
    Back          = 0x0020,
    LeftThumb     = 0x0040,
    RightThumb    = 0x0080,
    LeftShoulder  = 0x0100,
    RightShoulder = 0x0200,
    A             = 0x1000,
    B             = 0x2000,
    X             = 0x4000,
    Y             = 0x8000
}

// ═══════════════════════════════════════════════════════════════════════════
// XInput Reader
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Wraps the Windows XInput API via P/Invoke.
/// Handles controller discovery, polling, and raw value normalisation.
/// </summary>
public class XInputReader : IDisposable
{
    // Win32 error codes
    private const uint ErrorSuccess            = 0;
    private const uint ErrorDeviceNotConnected = 1167;

    // Axis range
    private const float AxisMax     = 32767f;
    private const float TriggerMax  = 255f;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState",
               CallingConvention = CallingConvention.StdCall)]
    private static extern uint NativeGetState(uint dwUserIndex, out XInputState pState);

    // ── Public state ────────────────────────────────────────────────────
    public int           UserIndex   { get; private set; } = -1;
    public bool          IsConnected { get; private set; }
    public XInputState   LastState   { get; private set; }
    public string        ControllerLabel => UserIndex >= 0 ? $"Controller {UserIndex + 1}" : "None";

    private bool _disposed;

    // ── Connection ──────────────────────────────────────────────────────

    /// <summary>Scan slots 0–3 and connect to the first active controller.</summary>
    public bool TryConnect()
    {
        for (int i = 0; i < 4; i++)
        {
            uint result = NativeGetState((uint)i, out var state);
            if (result == ErrorSuccess)
            {
                UserIndex   = i;
                IsConnected = true;
                LastState   = state;
                return true;
            }
        }
        UserIndex   = -1;
        IsConnected = false;
        return false;
    }

    /// <summary>Attempt to connect to a specific controller slot.</summary>
    public bool TryConnect(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex > 3) return false;
        uint result = NativeGetState((uint)slotIndex, out var state);
        if (result == ErrorSuccess)
        {
            UserIndex   = slotIndex;
            IsConnected = true;
            LastState   = state;
            return true;
        }
        return false;
    }

    // ── Polling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Poll the current controller state.
    /// Returns false if the controller has disconnected.
    /// </summary>
    public bool Poll()
    {
        if (UserIndex < 0) return false;

        uint result = NativeGetState((uint)UserIndex, out var state);
        if (result != ErrorSuccess)
        {
            IsConnected = false;
            return false;
        }

        IsConnected = true;
        LastState   = state;
        return true;
    }

    // ── Normalised accessors ─────────────────────────────────────────────

    /// <summary>Left stick as a normalised Vector2 in [-1, 1].</summary>
    public Vector2 LeftStick =>
        new(LastState.Gamepad.ThumbLX / AxisMax,
            LastState.Gamepad.ThumbLY / AxisMax);

    /// <summary>Right stick as a normalised Vector2 in [-1, 1].</summary>
    public Vector2 RightStick =>
        new(LastState.Gamepad.ThumbRX / AxisMax,
            LastState.Gamepad.ThumbRY / AxisMax);

    /// <summary>Left trigger in [0, 1].</summary>
    public float LeftTrigger  => LastState.Gamepad.LeftTrigger  / TriggerMax;

    /// <summary>Right trigger in [0, 1].</summary>
    public float RightTrigger => LastState.Gamepad.RightTrigger / TriggerMax;

    /// <summary>Raw button bitmask.</summary>
    public ushort Buttons => LastState.Gamepad.Buttons;

    /// <summary>Check whether a specific button is pressed.</summary>
    public bool IsButtonPressed(XInputButton button)
        => (LastState.Gamepad.Buttons & (ushort)button) != 0;

    // ── IDisposable ──────────────────────────────────────────────────────
    public void Dispose()
    {
        if (!_disposed)
        {
            IsConnected = false;
            _disposed   = true;
        }
    }
}
