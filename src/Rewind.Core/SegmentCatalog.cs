using System.Globalization;

namespace Rewind.Core;

public sealed record Segment(string Path, long Sequence, double Start, double End);
public sealed record ClipPlan(IReadOnlyList<Segment> Segments, double Start, double End)
{
    public double Duration => End - Start;
}

public static class SegmentCatalog
{
    public static IReadOnlyList<Segment> Parse(string content, string sessionDirectory)
    {
        var segments = new List<Segment>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields.Length != 3) continue;
            var name = fields[0].Trim('"', '\r');
            if (name != Path.GetFileName(name) || !TryGetSequence(name, out var sequence)) continue;
            if (!double.TryParse(fields[1], CultureInfo.InvariantCulture, out var start) ||
                !double.TryParse(fields[2], CultureInfo.InvariantCulture, out var end) ||
                !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start) continue;
            segments.Add(new Segment(Path.Combine(sessionDirectory, name), sequence, start, end));
        }
        return segments.OrderBy(s => s.Sequence).DistinctBy(s => s.Sequence).ToArray();
    }

    public static bool TryGetSequence(string filename, out long sequence)
    {
        sequence = 0;
        return filename.StartsWith("part-", StringComparison.Ordinal) && filename.EndsWith(".ts", StringComparison.Ordinal) &&
            long.TryParse(filename.AsSpan(5, filename.Length - 8), NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    public static ClipPlan Plan(IReadOnlyList<Segment> available, double targetEnd, int requestedSeconds)
    {
        if (available.Count == 0) throw new InvalidOperationException("The buffer is still warming up. Try again in a moment.");
        var ordered = available.OrderBy(s => s.Start).ToArray();
        var end = Math.Min(targetEnd, ordered[^1].End);
        var wantedStart = Math.Max(ordered[0].Start, end - requestedSeconds);
        var selected = ordered.Where(s => s.End > wantedStart && s.Start < end).ToArray();
        if (selected.Length == 0 || end <= selected[0].Start)
            throw new InvalidOperationException("There are no completed frames to save yet.");
        for (var i = 1; i < selected.Length; i++)
            if (selected[i].Sequence != selected[i - 1].Sequence + 1 || selected[i].Start - selected[i - 1].End > 0.15)
                throw new InvalidOperationException("The replay buffer has a gap. Restart capture and try again.");
        return new ClipPlan(selected, selected[0].Start, end);
    }

    public static long PruneBeforeSequence(IReadOnlyList<Segment> available, int bufferSeconds)
    {
        if (available.Count == 0) return -1;
        return available.Max(s => s.Sequence) - (int)Math.Ceiling(bufferSeconds / 2d) - 3;
    }
}
