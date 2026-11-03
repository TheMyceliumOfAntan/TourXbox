using System;

namespace TourXbox.Core.Input;

/// <summary>Xbox 手柄按键位掩码，数值与 XInput 的 XINPUT_GAMEPAD_* 常量一致。</summary>
[Flags]
public enum PadButton : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>手柄的归一化状态快照。</summary>
public readonly struct PadState
{
    public PadState(
        bool connected,
        ushort buttons,
        byte leftTrigger,
        byte rightTrigger,
        short leftX,
        short leftY,
        short rightX,
        short rightY,
        uint packetNumber)
    {
        Connected = connected;
        Buttons = buttons;
        LeftTrigger = leftTrigger;
        RightTrigger = rightTrigger;
        LeftX = leftX;
        LeftY = leftY;
        RightX = rightX;
        RightY = rightY;
        PacketNumber = packetNumber;
    }

    public bool Connected { get; }
    public ushort Buttons { get; }
    public byte LeftTrigger { get; }
    public byte RightTrigger { get; }
    public short LeftX { get; }
    public short LeftY { get; }
    public short RightX { get; }
    public short RightY { get; }
    public uint PacketNumber { get; }

    /// <summary>左摇杆水平，-1（左）~ +1（右）。</summary>
    public float Lx => LeftX < 0 ? LeftX / 32768f : LeftX / 32767f;

    /// <summary>左摇杆垂直，-1（下）~ +1（上）。XInput 的 Y 轴上为正。</summary>
    public float Ly => LeftY < 0 ? LeftY / 32768f : LeftY / 32767f;

    /// <summary>右摇杆水平。</summary>
    public float Rx => RightX < 0 ? RightX / 32768f : RightX / 32767f;

    /// <summary>右摇杆垂直。</summary>
    public float Ry => RightY < 0 ? RightY / 32768f : RightY / 32767f;

    /// <summary>左扳机 0~1。</summary>
    public float Lt => LeftTrigger / 255f;

    /// <summary>右扳机 0~1。</summary>
    public float Rt => RightTrigger / 255f;

    public bool IsDown(PadButton button) => (Buttons & (ushort)button) != 0;

    public static PadState Disconnected => new(false, 0, 0, 0, 0, 0, 0, 0, 0);

    public static string ButtonName(PadButton button) => button switch
    {
        PadButton.DPadUp => "十字键↑",
        PadButton.DPadDown => "十字键↓",
        PadButton.DPadLeft => "十字键←",
        PadButton.DPadRight => "十字键→",
        PadButton.Start => "Menu",
        PadButton.Back => "View",
        PadButton.LeftThumb => "L3",
        PadButton.RightThumb => "R3",
        PadButton.LeftShoulder => "LB",
        PadButton.RightShoulder => "RB",
        PadButton.A => "A",
        PadButton.B => "B",
        PadButton.X => "X",
        PadButton.Y => "Y",
        _ => button.ToString(),
    };
}
