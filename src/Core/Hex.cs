using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DriveDeck.Core
{
    /// <summary>Small, allocation-light hex helpers. All parsing is strict: anything that is not hex is rejected.</summary>
    public static class Hex
    {
        public static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
        }

        public static bool IsHexToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++) if (!IsHexChar(s[i])) return false;
            return true;
        }

        public static bool TryParseByte(string s, out byte value)
        {
            value = 0;
            if (s == null || s.Length != 2 || !IsHexToken(s)) return false;
            return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Parses "41 0C 1A F8" or "410C1AF8" into bytes. Returns null on any non-hex content or odd length.</summary>
        public static byte[] TryParseBytes(string s)
        {
            if (s == null) return null;
            var compact = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == ' ') continue;
                if (!IsHexChar(c)) return null;
                compact.Append(c);
            }
            if (compact.Length == 0 || compact.Length % 2 != 0) return null;
            var result = new byte[compact.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = byte.Parse(compact.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return result;
        }

        public static string ToHex(IList<byte> bytes, bool spaced = true)
        {
            if (bytes == null) return string.Empty;
            var sb = new StringBuilder(bytes.Count * 3);
            for (int i = 0; i < bytes.Count; i++)
            {
                if (spaced && i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
