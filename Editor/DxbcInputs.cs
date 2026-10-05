using System;
using System.Text;

namespace Okarin.AvatarTextureOptimizer.Editor
{
    // Reads the input signature (ISGN/ISG1 chunk) of a compiled Direct3D shader stage, to tell which semantics it
    // declares, including system values such as SV_VertexID that Unity's attribute list leaves out.
    internal static class DxbcInputs
    {
        internal static bool Has(byte[] bytes, string semantic)
        {
            if (bytes == null) return true; // Unreadable: assume it does.
            for (int start = 0; start + 32 < bytes.Length; start++)
            {
                if (bytes[start] != (byte)'D' || bytes[start + 1] != (byte)'X' || bytes[start + 2] != (byte)'B' || bytes[start + 3] != (byte)'C') continue;
                int chunks = BitConverter.ToInt32(bytes, start + 28);
                for (int c = 0; c < chunks; c++)
                {
                    int offset = start + BitConverter.ToInt32(bytes, start + 32 + 4 * c);
                    string code = Encoding.ASCII.GetString(bytes, offset, 4);
                    if (code != "ISGN" && code != "ISG1") continue;
                    int data = offset + 8, count = BitConverter.ToInt32(bytes, data);
                    int size = code == "ISGN" ? 24 : 32, skip = code == "ISGN" ? 0 : 4;
                    for (int e = 0; e < count; e++)
                    {
                        int name = data + BitConverter.ToInt32(bytes, data + 8 + e * size + skip);
                        var text = new StringBuilder();
                        while (name < bytes.Length && bytes[name] != 0) text.Append((char)bytes[name++]);
                        if (string.Equals(text.ToString(), semantic, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    return false;
                }
                return true; // No input signature found: assume it does.
            }
            return true;
        }
    }
}
