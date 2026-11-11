namespace Spark.Notes;

public enum DiffKind { Same, Changed, Removed, Added }

/// <summary>One aligned row of a side-by-side diff. Left/Right are null where that side has no line.</summary>
public sealed record DiffRow(DiffKind Kind, int? LeftNumber, string? Left, int? RightNumber, string? Right);

/// <summary>Line-based side-by-side diff (LCS over the region between the common prefix and suffix).</summary>
public static class LineDiff
{
    /// <summary>Above this many cells the middle section is shown as one replaced block instead of an exact LCS.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static IReadOnlyList<DiffRow> Compute(string left, string right)
    {
        var a = SplitLines(left);
        var b = SplitLines(right);
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;

        var ops = new List<(char Op, int A, int B)>(); // ' ' same, '-' left only, '+' right only
        for (int i = 0; i < prefix; i++) ops.Add((' ', i, i));
        Middle(a, b, prefix, a.Count - suffix, prefix, b.Count - suffix, ops);
        for (int i = suffix; i > 0; i--) ops.Add((' ', a.Count - i, b.Count - i));
        return ToRows(ops, a, b);
    }

    private static void Middle(IReadOnlyList<string> a, IReadOnlyList<string> b, int a0, int a1, int b0, int b1,
        List<(char, int, int)> ops)
    {
        int n = a1 - a0, m = b1 - b0;
        if ((long)n * m > MaxCells || n == 0 || m == 0)
        {
            for (int i = a0; i < a1; i++) ops.Add(('-', i, -1));
            for (int j = b0; j < b1; j++) ops.Add(('+', -1, j));
            return;
        }
        // lcs[i, j] = LCS length of a[a0+i..a1) and b[b0+j..b1)
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[a0 + i] == b[b0 + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[a0 + x] == b[b0 + y]) { ops.Add((' ', a0 + x, b0 + y)); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(('-', a0 + x, -1)); x++; }
            else { ops.Add(('+', -1, b0 + y)); y++; }
        }
        for (; x < n; x++) ops.Add(('-', a0 + x, -1));
        for (; y < m; y++) ops.Add(('+', -1, b0 + y));
    }

    /// <summary>Pairs each run of removals with the following run of additions as "changed" rows.</summary>
    private static List<DiffRow> ToRows(List<(char Op, int A, int B)> ops, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var rows = new List<DiffRow>();
        int k = 0;
        while (k < ops.Count)
        {
            if (ops[k].Op == ' ')
            {
                rows.Add(new DiffRow(DiffKind.Same, ops[k].A + 1, a[ops[k].A], ops[k].B + 1, b[ops[k].B]));
                k++;
                continue;
            }
            var removed = new List<int>();
            var added = new List<int>();
            while (k < ops.Count && ops[k].Op != ' ')
            {
                if (ops[k].Op == '-') removed.Add(ops[k].A); else added.Add(ops[k].B);
                k++;
            }
            for (int i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                int? l = i < removed.Count ? removed[i] : null;
                int? r = i < added.Count ? added[i] : null;
                var kind = l != null && r != null ? DiffKind.Changed : l != null ? DiffKind.Removed : DiffKind.Added;
                rows.Add(new DiffRow(kind, l + 1, l is { } li ? a[li] : null, r + 1, r is { } ri ? b[ri] : null));
            }
        }
        return rows;
    }
}
