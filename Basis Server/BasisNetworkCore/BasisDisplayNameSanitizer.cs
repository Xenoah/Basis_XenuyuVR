using System.Globalization;
using System.Text;

namespace Basis.Network.Core
{
    /// <summary>
    /// blank に描画される name が通らないよう、player display name を normalize する。
    /// control / format / 既知の invisible glyph を取り除き、Unicode whitespace を space に畳んで trim する。
    /// 描画可能なものが何も残らない場合は <see cref="string.Empty"/> を返す。
    /// </summary>
    public static class BasisDisplayNameSanitizer
    {
        private static readonly char[] InvisibleGlyphs =
        {
            (char)0x115F,
            (char)0x1160,
            (char)0x3164,
            (char)0xFFA0,
            (char)0x2800,
            (char)0x180E,
        };

        public static string Sanitize(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(displayName.Length);
            foreach (char character in displayName)
            {
                if (char.IsControl(character))
                {
                    continue;
                }
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
                {
                    continue;
                }
                if (IsInvisibleGlyph(character))
                {
                    continue;
                }
                builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
            }

            return builder.ToString().Trim();
        }

        public static bool IsValid(string displayName)
        {
            return !string.IsNullOrEmpty(Sanitize(displayName));
        }

        private static bool IsInvisibleGlyph(char character)
        {
            for (int index = 0; index < InvisibleGlyphs.Length; index++)
            {
                if (InvisibleGlyphs[index] == character)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
