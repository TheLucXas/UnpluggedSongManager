using System;

namespace USM.Core.Services.Quest
{
    public class QuestDevice
    {
        public string Serial { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Model { get; set; } = "Meta Quest";
        public bool IsWireless => Serial.Contains(":") || Serial.Contains(".");
        public string ConnectionType => IsWireless ? "Wi-Fi (Wireless)" : "USB (Wired)";
        public string DisplayName => $"{Model} [{ConnectionType}] ({Serial})";

        public override string ToString() => DisplayName;
    }
}
