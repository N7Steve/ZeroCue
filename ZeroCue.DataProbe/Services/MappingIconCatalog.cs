using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZeroCue.DataProbe.Models;

namespace ZeroCue.DataProbe.Services
{
    public enum MappingIconInputKind
    {
        Any,
        Gamepad,
        Keyboard,
        Mouse
    }

    public static class MappingIconCatalog
    {
        private const string AssetRoot = "avares://ZeroCue.DataProbe/Assets/gamepad-icons/";
        private static readonly Dictionary<string, Bitmap> IconCache = new();
        private static string _xGamepadVariant = "Default";
        private static string _keyboardMouseVariant = "Dark";

        public static string XGamepadVariant
        {
            get => _xGamepadVariant;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "Default" : value;
                if (!XGamepadVariantSuffixes.ContainsKey(normalized))
                    normalized = "Default";

                if (_xGamepadVariant == normalized)
                    return;

                _xGamepadVariant = normalized;
                IconCache.Clear();
            }
        }

        private static readonly IReadOnlyDictionary<string, string> XGamepadVariantSuffixes = new Dictionary<string, string>
        {
            ["Default"] = string.Empty,
            ["Light"] = "_Light",
            ["Retro"] = "_Retro",
            ["Alt"] = "_Alt",
            ["Alt 2"] = "_Alt_2"
        };

        public static string KeyboardMouseVariant
        {
            get => _keyboardMouseVariant;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "Dark" : value;
                if (!KeyboardMouseVariantSuffixes.ContainsKey(normalized))
                    normalized = "Dark";

                if (_keyboardMouseVariant == normalized)
                    return;

                _keyboardMouseVariant = normalized;
                IconCache.Clear();
            }
        }

        private static readonly IReadOnlyDictionary<string, string> KeyboardMouseVariantSuffixes = new Dictionary<string, string>
        {
            ["Dark"] = "_Dark",
            ["White"] = "_White",
            ["Alt"] = "_Alt",
            ["Retro"] = "_Retro",
            ["Vintage"] = "_Vintage"
        };

        private static readonly IReadOnlyDictionary<string, string> IconPaths = new Dictionary<string, string>
        {
            ["A"] = "XGamepad/Default/T_X_A_Color.png",
            ["B"] = "XGamepad/Default/T_X_B_Color.png",
            ["X"] = "XGamepad/Default/T_X_X_Color.png",
            ["Y"] = "XGamepad/Default/T_X_Y_Color.png",
            ["LB"] = "XGamepad/Default/T_X_LB.png",
            ["LeftShoulder"] = "XGamepad/Default/T_X_LB.png",
            ["RB"] = "XGamepad/Default/T_X_RB.png",
            ["RightShoulder"] = "XGamepad/Default/T_X_RB.png",
            ["LT"] = "XGamepad/Default/T_X_LT.png",
            ["LeftTrigger"] = "XGamepad/Default/T_X_LT.png",
            ["RT"] = "XGamepad/Default/T_X_RT.png",
            ["RightTrigger"] = "XGamepad/Default/T_X_RT.png",
            ["Up"] = "XGamepad/Default/T_X_Dpad_Up.png",
            ["Right"] = "XGamepad/Default/T_X_Dpad_Right.png",
            ["Down"] = "XGamepad/Default/T_X_Dpad_Down.png",
            ["Left"] = "XGamepad/Default/T_X_Dpad_Left.png",
            ["L3"] = "XGamepad/Default/T_X_Left_Stick_Click.png",
            ["LeftThumb"] = "XGamepad/Default/T_X_Left_Stick_Click.png",
            ["R3"] = "XGamepad/Default/T_X_Right_Stick_Click.png",
            ["RightThumb"] = "XGamepad/Default/T_X_Right_Stick_Click.png",
            ["Back"] = "XGamepad/Default/T_X_Share-1.png",
            ["Start"] = "XGamepad/Default/T_X_Share.png",
            ["Guide"] = "SGamepad/Default/T_S_Home.png",
            ["LS_Up"] = "XGamepad/Default/T_X_L_UP.png",
            ["LS_Down"] = "XGamepad/Default/T_X_L_Down.png",
            ["LS_Left"] = "XGamepad/Default/T_X_L_Left.png",
            ["LS_Right"] = "XGamepad/Default/T_X_L_Right.png",
            ["RS_Up"] = "XGamepad/Default/T_X_R_UP.png",
            ["RS_Down"] = "XGamepad/Default/T_X_R_Down.png",
            ["RS_Left"] = "XGamepad/Default/T_X_R_Left.png",
            ["RS_Right"] = "XGamepad/Default/T_X_R_Right.png",

            ["Escape"] = "Keyboard_Mouse/Dark/T_Esc_Key_Dark.png",
            ["F1"] = "Keyboard_Mouse/Dark/T_F1_Key_Dark.png",
            ["F2"] = "Keyboard_Mouse/Dark/T_F2_Key_Dark.png",
            ["F3"] = "Keyboard_Mouse/Dark/T_F3_Key_Dark.png",
            ["F4"] = "Keyboard_Mouse/Dark/T_F4_Key_Dark.png",
            ["F5"] = "Keyboard_Mouse/Dark/T_F5_Key_Dark.png",
            ["F6"] = "Keyboard_Mouse/Dark/T_F6_Key_Dark.png",
            ["F7"] = "Keyboard_Mouse/Dark/T_F7_Key_Dark.png",
            ["F8"] = "Keyboard_Mouse/Dark/T_F8_Key_Dark.png",
            ["F9"] = "Keyboard_Mouse/Dark/T_F9_Key_Dark.png",
            ["F10"] = "Keyboard_Mouse/Dark/T_F10_Key_Dark.png",
            ["F11"] = "Keyboard_Mouse/Dark/T_F11_Key_Dark.png",
            ["F12"] = "Keyboard_Mouse/Dark/T_F12_Key_Dark.png",
            ["Delete"] = "Keyboard_Mouse/Dark/T_Del_Key_Dark.png",
            ["OemTilde"] = "Keyboard_Mouse/Dark/T_Tilde_Key_Dark.png",
            ["D1"] = "Keyboard_Mouse/Dark/T_1_Key_Dark.png",
            ["D2"] = "Keyboard_Mouse/Dark/T_2_Key_Dark.png",
            ["D3"] = "Keyboard_Mouse/Dark/T_3_Key_Dark.png",
            ["D4"] = "Keyboard_Mouse/Dark/T_4_Key_Dark.png",
            ["D5"] = "Keyboard_Mouse/Dark/T_5_Key_Dark.png",
            ["D6"] = "Keyboard_Mouse/Dark/T_6_Key_Dark.png",
            ["D7"] = "Keyboard_Mouse/Dark/T_7_Key_Dark.png",
            ["D8"] = "Keyboard_Mouse/Dark/T_8_Key_Dark.png",
            ["D9"] = "Keyboard_Mouse/Dark/T_9_Key_Dark.png",
            ["D0"] = "Keyboard_Mouse/Dark/T_0_Key_Dark.png",
            ["Subtract"] = "Keyboard_Mouse/Dark/T_Minus_Key_Dark.png",
            ["Add"] = "Keyboard_Mouse/Dark/T_Plus_Key_Dark.png",
            ["Backspace"] = "Keyboard_Mouse/Dark/T_BackSpace_Key_Dark.png",
            ["BackSpace"] = "Keyboard_Mouse/Dark/T_BackSpace_Key_Dark.png",
            ["Tab"] = "Keyboard_Mouse/Dark/T_Tab_Key_Dark.png",
            ["Q"] = "Keyboard_Mouse/Dark/T_Q_Key_Dark.png",
            ["KeyQ"] = "Keyboard_Mouse/Dark/T_Q_Key_Dark.png",
            ["W"] = "Keyboard_Mouse/Dark/T_W_Key_Dark.png",
            ["KeyW"] = "Keyboard_Mouse/Dark/T_W_Key_Dark.png",
            ["E"] = "Keyboard_Mouse/Dark/T_E_Key_Dark.png",
            ["KeyE"] = "Keyboard_Mouse/Dark/T_E_Key_Dark.png",
            ["R"] = "Keyboard_Mouse/Dark/T_R_Key_Dark.png",
            ["KeyR"] = "Keyboard_Mouse/Dark/T_R_Key_Dark.png",
            ["T"] = "Keyboard_Mouse/Dark/T_T_Key_Dark.png",
            ["KeyT"] = "Keyboard_Mouse/Dark/T_T_Key_Dark.png",
            ["U"] = "Keyboard_Mouse/Dark/T_U_Key_Dark.png",
            ["KeyU"] = "Keyboard_Mouse/Dark/T_U_Key_Dark.png",
            ["I"] = "Keyboard_Mouse/Dark/T_I_Key_Dark.png",
            ["KeyI"] = "Keyboard_Mouse/Dark/T_I_Key_Dark.png",
            ["O"] = "Keyboard_Mouse/Dark/T_O_Key_Dark.png",
            ["KeyO"] = "Keyboard_Mouse/Dark/T_O_Key_Dark.png",
            ["P"] = "Keyboard_Mouse/Dark/T_P_Key_Dark.png",
            ["KeyP"] = "Keyboard_Mouse/Dark/T_P_Key_Dark.png",
            ["OemOpenBrackets"] = "Keyboard_Mouse/Dark/T_Brackets_L_Key_Dark.png",
            ["OemCloseBrackets"] = "Keyboard_Mouse/Dark/T_Brackets_R_Key_Dark.png",
            ["OemPipe"] = "Keyboard_Mouse/Dark/T_Slash_Key_Dark.png",
            ["Capital"] = "Keyboard_Mouse/Dark/T_CapsLock_Key_Dark.png",
            ["KeyA"] = "Keyboard_Mouse/Dark/T_A_Key_Dark.png",
            ["KeyB"] = "Keyboard_Mouse/Dark/T_B_Key_Dark.png",
            ["S"] = "Keyboard_Mouse/Dark/T_S_Key_Dark.png",
            ["KeyS"] = "Keyboard_Mouse/Dark/T_S_Key_Dark.png",
            ["D"] = "Keyboard_Mouse/Dark/T_D_Key_Dark.png",
            ["KeyD"] = "Keyboard_Mouse/Dark/T_D_Key_Dark.png",
            ["F"] = "Keyboard_Mouse/Dark/T_F_Key_Dark.png",
            ["KeyF"] = "Keyboard_Mouse/Dark/T_F_Key_Dark.png",
            ["G"] = "Keyboard_Mouse/Dark/T_G_Key_Dark.png",
            ["KeyG"] = "Keyboard_Mouse/Dark/T_G_Key_Dark.png",
            ["H"] = "Keyboard_Mouse/Dark/T_H_Key_Dark.png",
            ["KeyH"] = "Keyboard_Mouse/Dark/T_H_Key_Dark.png",
            ["J"] = "Keyboard_Mouse/Dark/T_J_Key_Dark.png",
            ["KeyJ"] = "Keyboard_Mouse/Dark/T_J_Key_Dark.png",
            ["K"] = "Keyboard_Mouse/Dark/T_K_Key_Dark.png",
            ["KeyK"] = "Keyboard_Mouse/Dark/T_K_Key_Dark.png",
            ["L"] = "Keyboard_Mouse/Dark/T_L_Key_Dark.png",
            ["KeyL"] = "Keyboard_Mouse/Dark/T_L_Key_Dark.png",
            ["OemSemicolon"] = "Keyboard_Mouse/Dark/T_Semicolon_Key_Dark.png",
            ["OemQuotes"] = "Keyboard_Mouse/Dark/T_Quotation_Key_Dark.png",
            ["Return"] = "Keyboard_Mouse/Dark/T_Enter_Key_Dark.png",
            ["LeftShift"] = "Keyboard_Mouse/Dark/T_Shift_Key_Dark.png",
            ["Z"] = "Keyboard_Mouse/Dark/T_Z_Key_Dark.png",
            ["KeyZ"] = "Keyboard_Mouse/Dark/T_Z_Key_Dark.png",
            ["KeyX"] = "Keyboard_Mouse/Dark/T_X_Key_Dark.png",
            ["C"] = "Keyboard_Mouse/Dark/T_C_Key_Dark.png",
            ["KeyC"] = "Keyboard_Mouse/Dark/T_C_Key_Dark.png",
            ["V"] = "Keyboard_Mouse/Dark/T_V_Key_Dark.png",
            ["KeyV"] = "Keyboard_Mouse/Dark/T_V_Key_Dark.png",
            ["KeyY"] = "Keyboard_Mouse/Dark/T_Y_Key_Dark.png",
            ["N"] = "Keyboard_Mouse/Dark/T_N_Key_Dark.png",
            ["KeyN"] = "Keyboard_Mouse/Dark/T_N_Key_Dark.png",
            ["M"] = "Keyboard_Mouse/Dark/T_M_Key_Dark.png",
            ["KeyM"] = "Keyboard_Mouse/Dark/T_M_Key_Dark.png",
            ["OemQuestion"] = "Keyboard_Mouse/Dark/T_Question_Mark_Key_Dark.png",
            ["RightShift"] = "Keyboard_Mouse/Dark/T_Shift_Key_Dark.png",
            ["LeftCtrl"] = "Keyboard_Mouse/Dark/T_Crtl_Key_Dark.png",
            ["LWin"] = "Keyboard_Mouse/Dark/T_Keyboard_Mouse_Key_Sprite.png",
            ["LeftAlt"] = "Keyboard_Mouse/Dark/T_Alt_Key_Dark.png",
            ["Space"] = "Keyboard_Mouse/Dark/T_Space_Key_Dark.png",
            ["RightAlt"] = "Keyboard_Mouse/Dark/T_Alt_Key_Dark.png",
            ["RightCtrl"] = "Keyboard_Mouse/Dark/T_Crtl_Key_Dark.png",
            ["Insert"] = "Keyboard_Mouse/Dark/T_Ins_Key_Dark.png",
            ["Home"] = "Keyboard_Mouse/Dark/T_Home_Key_Dark.png",
            ["End"] = "Keyboard_Mouse/Dark/T_End_Key_Dark.png",
            ["PageUp"] = "Keyboard_Mouse/Dark/T_PageUp_Key_Dark.png",
            ["PageDown"] = "Keyboard_Mouse/Dark/T_PageDown_Key_Dark.png",
            ["NumLock"] = "Keyboard_Mouse/Dark/T_NumLock_Key_Dark.png",
            ["Divide"] = "Keyboard_Mouse/Dark/T_Slash_Key_Dark.png",
            ["Multiply"] = "Keyboard_Mouse/Dark/T_Asterisk_Key_Dark.png",
            ["KeyUp"] = "Keyboard_Mouse/Dark/T_Up_Key_Dark.png",
            ["KeyDown"] = "Keyboard_Mouse/Dark/T_Down_Key_Dark.png",
            ["KeyLeft"] = "Keyboard_Mouse/Dark/T_Left_Key_Dark.png",
            ["KeyRight"] = "Keyboard_Mouse/Dark/T_Right_Key_Dark.png",

            ["MouseLeft"] = "Keyboard_Mouse/Dark/T_Mouse_Left_Key_Dark.png",
            ["MouseRight"] = "Keyboard_Mouse/Dark/T_Mouse_Right_Key_Dark.png",
            ["MouseMiddle"] = "Keyboard_Mouse/Dark/T_Mouse_Middle_Key_Dark.png",
            ["MouseX1"] = "Keyboard_Mouse/Dark/T_Mouse_X_Key_Dark.png",
            ["MouseX2"] = "Keyboard_Mouse/Dark/T_Mouse_Y_Key_Dark.png",
            ["ScrollUp"] = "Keyboard_Mouse/Dark/T_Mouse_Scroll_Up_Key_Dark_Key_Dark.png",
            ["ScrollDown"] = "Keyboard_Mouse/Dark/T_Mouse_Scroll_Down_Key_Dark_Key_Dark.png",
            ["MouseMoveUp"] = "Keyboard_Mouse/Dark/T_Mouse_XY_Key_Dark.png",
            ["MouseMoveDown"] = "Keyboard_Mouse/Dark/T_Mouse_XY_Key_Dark.png",
            ["MouseMoveLeft"] = "Keyboard_Mouse/Dark/T_Mouse_XY_Key_Dark.png",
            ["MouseMoveRight"] = "Keyboard_Mouse/Dark/T_Mouse_XY_Key_Dark.png"
        };

        private static readonly IReadOnlyDictionary<string, string> KeyboardPreferredIconPaths = new Dictionary<string, string>
        {
            ["A"] = "Keyboard_Mouse/Dark/T_A_Key_Dark.png",
            ["B"] = "Keyboard_Mouse/Dark/T_B_Key_Dark.png",
            ["X"] = "Keyboard_Mouse/Dark/T_X_Key_Dark.png",
            ["Y"] = "Keyboard_Mouse/Dark/T_Y_Key_Dark.png",
            ["Back"] = "Keyboard_Mouse/Dark/T_BackSpace_Key_Dark.png",
            ["Up"] = "Keyboard_Mouse/Dark/T_Up_Key_Dark.png",
            ["Down"] = "Keyboard_Mouse/Dark/T_Down_Key_Dark.png",
            ["Left"] = "Keyboard_Mouse/Dark/T_Left_Key_Dark.png",
            ["Right"] = "Keyboard_Mouse/Dark/T_Right_Key_Dark.png"
        };

        public static Bitmap? GetBitmap(string mapping)
            => GetBitmap(mapping, MappingIconInputKind.Any);

        public static Bitmap? GetBitmap(string mapping, MappingIconInputKind inputKind)
        {
            var uri = GetIconUri(mapping, inputKind);
            if (string.IsNullOrEmpty(uri))
                return null;

            if (IconCache.TryGetValue(uri, out var cached))
                return cached;

            try
            {
                var assetUri = new Uri(uri);
                if (!AssetLoader.Exists(assetUri))
                    return null;

                var bitmap = new Bitmap(AssetLoader.Open(assetUri));
                IconCache[uri] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public static string? GetIconUri(string mapping)
            => GetIconUri(mapping, MappingIconInputKind.Any);

        public static string? GetIconUri(string mapping, MappingIconInputKind inputKind)
        {
            if (string.IsNullOrWhiteSpace(mapping) || mapping == "Sin Mapeo")
                return null;

            if (mapping.StartsWith("Action:"))
                return null;

            var normalized = Normalize(mapping);
            string? relativePath = null;

            if (inputKind == MappingIconInputKind.Keyboard)
                KeyboardPreferredIconPaths.TryGetValue(normalized, out relativePath);

            if (string.IsNullOrEmpty(relativePath) && !IconPaths.TryGetValue(normalized, out relativePath))
                return null;

            relativePath = ApplyXGamepadVariant(relativePath);
            relativePath = ApplyKeyboardMouseVariant(relativePath);
            return AssetRoot + relativePath;
        }

        public static string GetFallbackText(string mapping)
        {
            if (string.IsNullOrWhiteSpace(mapping) || mapping == "Sin Mapeo")
                return string.Empty;

            if (mapping.StartsWith("Action:EcoMode"))
                return "Icon:Leaf";

            if (mapping.StartsWith("Action:LoadProfile"))
                return "Icon:AccountSwitch";

            return Normalize(mapping) switch
            {
                "Paddle_L4" => "L4",
                "Paddle_L5" => "L5",
                "Paddle_R4" => "R4",
                "Paddle_R5" => "R5",
                "SAX_L" => "SL",
                "SAX_R" => "SR",
                "LeftShoulder" => "LB",
                "RightShoulder" => "RB",
                "LeftTrigger" => "LT",
                "RightTrigger" => "RT",
                "LeftThumb" => "L3",
                "RightThumb" => "R3",
                "KeyA" => "A",
                "KeyB" => "B",
                "KeyC" => "C",
                "KeyD" => "D",
                "KeyE" => "E",
                "KeyF" => "F",
                "KeyG" => "G",
                "KeyH" => "H",
                "KeyI" => "I",
                "KeyJ" => "J",
                "KeyK" => "K",
                "KeyL" => "L",
                "KeyM" => "M",
                "KeyN" => "N",
                "KeyO" => "O",
                "KeyP" => "P",
                "KeyQ" => "Q",
                "KeyR" => "R",
                "KeyS" => "S",
                "KeyT" => "T",
                "KeyU" => "U",
                "KeyV" => "V",
                "KeyW" => "W",
                "KeyX" => "X",
                "KeyY" => "Y",
                "KeyZ" => "Z",
                "Return" => "Ent",
                "Backspace" or "BackSpace" => "Bspc",
                "Escape" => "Esc",
                "Delete" => "Del",
                "Insert" => "Ins",
                "PageUp" => "PgUp",
                "PageDown" => "PgDn",
                "OemTilde" => "`",
                "OemOpenBrackets" => "[",
                "OemCloseBrackets" => "]",
                "OemPipe" => "\\",
                "OemSemicolon" => ";",
                "OemQuotes" => "'",
                "OemComma" => ",",
                "OemPeriod" => ".",
                "OemQuestion" => "/",
                "Decimal" or "NumpadDecimal" => ".",
                "KeyUp" => "Up",
                "KeyDown" => "Dn",
                "KeyLeft" => "Left",
                "KeyRight" => "Right",
                "Capital" => "Caps",
                "LeftCtrl" or "RightCtrl" => "Ctrl",
                "LeftAlt" or "RightAlt" => "Alt",
                "LeftShift" or "RightShift" => "Shift",
                "Shift" => "Shift",
                "LWin" => "Win",
                "MouseLeft" => "ML",
                "MouseRight" => "MR",
                "MouseMiddle" => "M3",
                "MouseX1" => "M4",
                "MouseX2" => "M5",
                "ScrollUp" => "ScrU",
                "ScrollDown" => "ScrD",
                "MouseMoveUp" => "MUp",
                "MouseMoveDown" => "MDn",
                "MouseMoveLeft" => "MLt",
                "MouseMoveRight" => "MRt",
                var value when value.StartsWith("NumPad", StringComparison.Ordinal) => "N" + value["NumPad".Length..],
                var value when value.Length <= 4 => value,
                _ => string.Empty
            };
        }

        private static string Normalize(string mapping)
        {
            mapping = VirtualTarget.GetBaseTarget(mapping);
            return mapping switch
            {
                "Back" => "Back",
                "Y" => IconPaths.ContainsKey("Y") ? "Y" : mapping,
                "OemComma" => "OemComma",
                "OemPeriod" => "OemPeriod",
                _ => mapping
            };
        }

        private static string ApplyXGamepadVariant(string relativePath)
        {
            if (_xGamepadVariant == "Default" || !relativePath.StartsWith("XGamepad/Default/"))
                return relativePath;

            var suffix = XGamepadVariantSuffixes[_xGamepadVariant];
            var fileNameStart = relativePath.LastIndexOf('/') + 1;
            var fileName = relativePath[fileNameStart..];
            var folder = $"XGamepad/{_xGamepadVariant}/";

            if (!fileName.EndsWith(".png"))
                return relativePath;

            var stem = fileName[..^4];
            var variantFileName = stem.EndsWith("-1")
                ? $"{stem[..^2]}{suffix}-1.png"
                : $"{stem}{suffix}.png";
            var variantPath = folder + variantFileName;

            return AssetLoader.Exists(new System.Uri(AssetRoot + variantPath))
                ? variantPath
                : relativePath;
        }

        private static string ApplyKeyboardMouseVariant(string relativePath)
        {
            if (_keyboardMouseVariant == "Dark" || !relativePath.StartsWith("Keyboard_Mouse/Dark/"))
                return relativePath;

            var suffix = KeyboardMouseVariantSuffixes[_keyboardMouseVariant];
            var fileNameStart = relativePath.LastIndexOf('/') + 1;
            var fileName = relativePath[fileNameStart..];
            var folder = $"Keyboard_Mouse/{_keyboardMouseVariant}/";

            if (!fileName.EndsWith(".png"))
                return relativePath;

            var stem = fileName[..^4];
            string variantFileName;

            if (stem == "T_Keyboard_Mouse_Key_Sprite")
            {
                variantFileName = $"T_Keyboard_Mouse_Key{suffix}_Sprite.png";
            }
            else if (stem.EndsWith("_Dark-1"))
            {
                variantFileName = $"{stem[..^7]}{suffix}-1.png";
            }
            else if (stem.EndsWith("_Dark"))
            {
                variantFileName = $"{stem[..^5]}{suffix}.png";
            }
            else
            {
                return relativePath;
            }

            var variantPath = folder + variantFileName;
            return AssetLoader.Exists(new System.Uri(AssetRoot + variantPath))
                ? variantPath
                : relativePath;
        }
    }
}
