using System;
using System.Runtime.InteropServices;

namespace ZeroCue.DataProbe.Services
{
    public static class KeyboardSimulator
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint INPUT_MOUSE = 0;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint XBUTTON1 = 0x0001;
        private const uint XBUTTON2 = 0x0002;
        private const int WHEEL_DELTA = 120;

        public static void KeyDown(string keyName)
        {
            if (TryMouseButton(keyName, true)) return;

            ushort vk = GetVirtualKey(keyName);
            if (vk == 0) return;

            INPUT input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = 0
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void KeyUp(string keyName)
        {
            if (TryMouseButton(keyName, false)) return;

            ushort vk = GetVirtualKey(keyName);
            if (vk == 0) return;

            INPUT input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = KEYEVENTF_KEYUP
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Pulse(string inputName)
        {
            if (inputName == "ScrollUp")
            {
                SendMouse(MOUSEEVENTF_WHEEL, unchecked((uint)WHEEL_DELTA));
                return;
            }

            if (inputName == "ScrollDown")
            {
                SendMouse(MOUSEEVENTF_WHEEL, unchecked((uint)-WHEEL_DELTA));
                return;
            }

            KeyDown(inputName);
            KeyUp(inputName);
        }

        private static bool TryMouseButton(string keyName, bool isDown)
        {
            switch (keyName)
            {
                case "MouseLeft":
                    SendMouse(isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0);
                    return true;
                case "MouseRight":
                    SendMouse(isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0);
                    return true;
                case "MouseMiddle":
                    SendMouse(isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0);
                    return true;
                case "MouseX1":
                    SendMouse(isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1);
                    return true;
                case "MouseX2":
                    SendMouse(isDown ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2);
                    return true;
                case "ScrollUp" when isDown:
                    SendMouse(MOUSEEVENTF_WHEEL, unchecked((uint)WHEEL_DELTA));
                    return true;
                case "ScrollDown" when isDown:
                    SendMouse(MOUSEEVENTF_WHEEL, unchecked((uint)-WHEEL_DELTA));
                    return true;
                case "ScrollUp":
                case "ScrollDown":
                    return true;
                default:
                    return false;
            }
        }

        private static void SendMouse(uint flags, uint mouseData)
        {
            INPUT input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = mouseData,
                        dwFlags = flags
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static ushort GetVirtualKey(string keyName)
        {
            if (keyName.StartsWith("Key", StringComparison.OrdinalIgnoreCase) &&
                keyName.Length == 4 &&
                char.IsLetter(keyName[3]))
            {
                return (ushort)char.ToUpperInvariant(keyName[3]);
            }

            if (keyName.Length == 1 && char.IsLetterOrDigit(keyName[0]))
            {
                char upper = char.ToUpperInvariant(keyName[0]);
                if (upper >= 'A' && upper <= 'Z') return (ushort)upper;
                if (upper >= '0' && upper <= '9') return (ushort)upper;
            }

            return keyName.ToUpperInvariant() switch
            {
                "LSHIFT" => 0xA0,
                "RSHIFT" => 0xA1,
                "LCTRL" => 0xA2,
                "RCTRL" => 0xA3,
                "LALT" => 0xA4,
                "RALT" => 0xA5,
                "SPACE" => 0x20,
                "ENTER" => 0x0D,
                "RETURN" => 0x0D,
                "ESCAPE" => 0x1B,
                "BACK" => 0x08,
                "BACKSPACE" => 0x08,
                "TAB" => 0x09,
                "UP" => 0x26,
                "KEYUP" => 0x26,
                "DOWN" => 0x28,
                "KEYDOWN" => 0x28,
                "LEFT" => 0x25,
                "KEYLEFT" => 0x25,
                "RIGHT" => 0x27,
                "KEYRIGHT" => 0x27,
                "DELETE" => 0x2E,
                "INSERT" => 0x2D,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" => 0x21,
                "PAGEDOWN" => 0x22,
                "CAPITAL" => 0x14,
                "CAPSLOCK" => 0x14,
                "LWIN" => 0x5B,
                "RWIN" => 0x5C,
                "D0" => 0x30,
                "D1" => 0x31,
                "D2" => 0x32,
                "D3" => 0x33,
                "D4" => 0x34,
                "D5" => 0x35,
                "D6" => 0x36,
                "D7" => 0x37,
                "D8" => 0x38,
                "D9" => 0x39,
                "NUMPAD0" => 0x60,
                "NUMPAD1" => 0x61,
                "NUMPAD2" => 0x62,
                "NUMPAD3" => 0x63,
                "NUMPAD4" => 0x64,
                "NUMPAD5" => 0x65,
                "NUMPAD6" => 0x66,
                "NUMPAD7" => 0x67,
                "NUMPAD8" => 0x68,
                "NUMPAD9" => 0x69,
                "MULTIPLY" => 0x6A,
                "ADD" => 0x6B,
                "SUBTRACT" => 0x6D,
                "DECIMAL" => 0x6E,
                "NUMPADDECIMAL" => 0x6E,
                "DIVIDE" => 0x6F,
                "NUMLOCK" => 0x90,
                "OEMTILDE" => 0xC0,
                "OEMOPENBRACKETS" => 0xDB,
                "OEMCLOSEBRACKETS" => 0xDD,
                "OEMPIPE" => 0xDC,
                "OEMSEMICOLON" => 0xBA,
                "OEMQUOTES" => 0xDE,
                "OEMCOMMA" => 0xBC,
                "OEMPERIOD" => 0xBE,
                "OEMQUESTION" => 0xBF,
                _ => 0
            };
        }
    }
}
