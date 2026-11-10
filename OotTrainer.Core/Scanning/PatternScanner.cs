using System.Globalization;
using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Scanning;

/// <summary>Byte pattern with wildcards, e.g. "1F 20 03 D5 ?? ?? 00 94".</summary>
public sealed class Pattern
{
    public byte[] Bytes { get; }
    public bool[] Fixed { get; }
    public int Length => Bytes.Length;

    private Pattern(byte[] bytes, bool[] fixedMask)
    {
        Bytes = bytes;
        Fixed = fixedMask;
    }

    public static Pattern Parse(string text)
    {
        var tokens = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new FormatException("Empty pattern.");
        var bytes = new byte[tokens.Length];
        var fixedMask = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "?" or "??" or "*") continue;
            if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"Bad byte '{tokens[i]}'. Use hex pairs or ??.");
            fixedMask[i] = true;
        }
        if (!fixedMask.Any(f => f)) throw new FormatException("Pattern must contain at least one fixed byte.");
        return new Pattern(bytes, fixedMask);
    }

    internal bool MatchesAt(ReadOnlySpan<byte> data, int index)
    {
        for (int j = 0; j < Bytes.Length; j++)
            if (Fixed[j] && data[index + j] != Bytes[j]) return false;
        return true;
    }
}

public static class PatternScanner
{
    /// <summary>Find a pattern in the guest window. Returns guest addresses in ascending order.</summary>
    public static List<ulong> Scan(GuestMap map, Pattern pattern, bool writableOnly = false, int maxResults = 1000)
    {
        int anchor = Array.FindIndex(pattern.Fixed, f => f); // first fixed byte, used to skip quickly
        var plan = Chunks.Plan(map.GuestRegions(writableOnly), pattern.Length - 1).ToList();
        var found = new List<ulong>();

        Parallel.ForEach(plan,
            () => new byte[Chunks.Size + pattern.Length],
            (chunk, state, buf) =>
            {
                if (found.Count >= maxResults) { state.Stop(); return buf; }
                if (!map.Process.TryRead(chunk.Host, buf.AsSpan(0, chunk.Length))) return buf;

                var data = buf.AsSpan(0, chunk.Length);
                int limit = Math.Min(chunk.Owned, chunk.Length - pattern.Length + 1);
                int i = 0;
                while (i < limit)
                {
                    int hit = data.Slice(i + anchor, limit - i).IndexOf(pattern.Bytes[anchor]);
                    if (hit < 0) break;
                    i += hit;
                    if (pattern.MatchesAt(data, i))
                        lock (found) found.Add(map.ToGuest(chunk.Host) + (ulong)i);
                    i++;
                }
                return buf;
            },
            _ => { });

        found.Sort();
        return found.Count > maxResults ? found.GetRange(0, maxResults) : found;
    }
}
