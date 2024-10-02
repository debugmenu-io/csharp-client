using System;

#nullable enable

namespace DebugMenuIO {
    public class DebugMenuChannelAttribute : Attribute {
        public string? Name { get; set; } = null;
        public DebugMenuColor? Color { get; set; } = null;
        public string? Path { get; set; } = null;
    }

    public enum DebugMenuColor {
        Red,
        Green,
        Blue,
        Grey,
        Teal
    }

    public class ControllerAttribute : Attribute {
        public string? Path { get; set; } = null;
    }

    public class ToggleAttribute : DebugMenuChannelAttribute {
    }

    public class TextFieldAttribute : DebugMenuChannelAttribute {
        public int MaxLength { get; set; }
    }

    public class ButtonAttribute : DebugMenuChannelAttribute {
    }
}
