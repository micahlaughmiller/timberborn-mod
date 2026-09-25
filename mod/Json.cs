using System.Text;

namespace TimberbornAI
{
    /// <summary>Minimal JSON writing helpers; avoids pulling a serializer into the game process.</summary>
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Reads a top-level string field out of a flat JSON object. Good enough for command payloads.</summary>
        public static string Field(string json, string name)
        {
            int k = json.IndexOf("\"" + name + "\"");
            if (k < 0) return null;
            int colon = json.IndexOf(':', k);
            if (colon < 0) return null;

            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;

            if (json[i] == '"')
            {
                var sb = new StringBuilder();
                for (i++; i < json.Length && json[i] != '"'; i++)
                {
                    if (json[i] == '\\' && i + 1 < json.Length) i++;
                    sb.Append(json[i]);
                }
                return sb.ToString();
            }

            int end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
            return json.Substring(i, end - i).Trim();
        }

        public static int Int(string json, string name, int fallback)
            => int.TryParse(Field(json, name), out var v) ? v : fallback;
    }
}
