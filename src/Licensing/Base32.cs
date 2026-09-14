using System;
using System.Text;

namespace PitMine3D.Kylin.Licensing
{
    /// <summary>
    /// 人眼友好的 Base32（Crockford 变体）。字母表剔掉了 I/L/O/U —— 用户要在电话/微信里
    /// 转抄机器码，1 和 I、0 和 O 混淆是最常见的报错来源；解码时把它们按形近字规约回去。
    /// </summary>
    internal static class Base32
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // 32 字符，无 I/L/O/U

        /// <summary>字节流 → Base32 文本（高位在前，末尾按 5bit 补零，不加填充符）。</summary>
        public static string Encode(ReadOnlySpan<byte> data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(Alphabet[(buffer >> bits) & 31]);
                }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        /// <summary>
        /// Base32 文本 → 字节流。忽略分隔符（空格/连字符/换行），形近字自动规约，大小写不敏感。
        /// 出现字母表以外的字符返回 null（调用方按"激活码格式错误"处理，不抛异常）。
        /// </summary>
        public static byte[]? Decode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var bytes = new System.Collections.Generic.List<byte>(text.Length * 5 / 8 + 1);
            int buffer = 0, bits = 0;
            foreach (char raw in text)
            {
                if (raw is '-' or ' ' or '\t' or '\r' or '\n' or '_') continue;

                char c = char.ToUpperInvariant(raw);
                c = c switch
                {
                    'I' or 'L' => '1',      // 形近规约：I/l → 1
                    'O' => '0',             //           O   → 0
                    'U' => 'V',             //           U   → V
                    _ => c
                };
                int v = Alphabet.IndexOf(c);
                if (v < 0) return null;

                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            return bytes.ToArray();
        }

        /// <summary>按 <paramref name="group"/> 个字符插一个连字符，便于抄写。</summary>
        public static string Group(string s, int group)
        {
            if (group <= 0 || s.Length <= group) return s;
            var sb = new StringBuilder(s.Length + s.Length / group);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && i % group == 0) sb.Append('-');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
