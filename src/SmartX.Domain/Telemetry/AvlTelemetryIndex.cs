namespace SmartX.Domain.Telemetry;

/// <summary>
/// A balanced, in-memory timestamp index for one sensor's strongly typed readings.
///
/// A separate index should be created for each sensor and payload type. This class
/// does not replace Smart-X's existing append-order telemetry repository: P3-03
/// will wire the same accepted TelemetryPacket instances into both structures.
///
/// Inserts preserve equal-timestamp readings in stable arrival order; bounded
/// range queries traverse only relevant AVL branches and return read-only copies.
/// </summary>
/// <typeparam name="T">The native telemetry value type (double, int or bool).</typeparam>
public sealed class AvlTelemetryIndex<T>
    where T : struct
{
    /// <summary>Default number of packets returned by a historical range query.</summary>
    public const int DefaultRangeLimit = 100;

    /// <summary>
    /// Upper bound for one query, preventing a client from copying an unbounded
    /// historical series into memory in a single request.
    /// </summary>
    public const int MaxRangeLimit = 10_000;

    private readonly object _sync = new();
    private Node? _root;
    private int _count;
    private int _distinctTimestampCount;

    /// <summary>The number of indexed packets, including equal-time readings.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>The number of distinct UTC timestamps represented by tree nodes.</summary>
    public int DistinctTimestampCount
    {
        get
        {
            lock (_sync)
            {
                return _distinctTimestampCount;
            }
        }
    }

    /// <summary>The tree height, with an empty tree having height zero.</summary>
    public int Height
    {
        get
        {
            lock (_sync)
            {
                return GetHeight(_root);
            }
        }
    }

    /// <summary>
    /// Exposes the root timestamp for structural verification and teaching evidence.
    /// No mutable tree node is returned.
    /// </summary>
    public DateTimeOffset? RootTimestampUtc
    {
        get
        {
            lock (_sync)
            {
                return _root?.TimestampUtc;
            }
        }
    }

    /// <summary>
    /// Indexes the exact accepted packet. A matching timestamp adds to its bucket
    /// rather than replacing an earlier packet. All comparisons use UTC instants.
    /// Average and worst-case insertion are O(log n) in the number of distinct keys.
    /// </summary>
    public void Insert(TelemetryPacket<T> packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_sync)
        {
            _root = InsertNode(_root, packet.CapturedAtUtc.ToUniversalTime(), packet);
            _count++;
        }
    }

    /// <summary>
    /// Returns an inclusive, timestamp-ordered snapshot of telemetry captured
    /// between the two supplied instants. Offsets are canonicalised to UTC,
    /// while packets sharing a timestamp retain their original insertion order.
    /// </summary>
    /// <remarks>
    /// AVL branch pruning avoids scanning all historical readings. For n unique
    /// timestamps and k returned packets, a balanced lookup takes
    /// O(log n + k) time, excluding the cost of copying the returned packets.
    /// The result is bounded, read-only and independent of future insertions.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The start instant is later than the end instant.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The requested result limit is outside the supported range.
    /// </exception>
    public IReadOnlyList<TelemetryPacket<T>> QueryRange(
        DateTimeOffset fromInclusive,
        DateTimeOffset toInclusive,
        int limit = DefaultRangeLimit)
    {
        if (limit < 1 || limit > MaxRangeLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit,
                $"Range limit must be between 1 and {MaxRangeLimit}.");
        }

        DateTimeOffset fromUtc = fromInclusive.ToUniversalTime();
        DateTimeOffset toUtc = toInclusive.ToUniversalTime();
        if (fromUtc > toUtc)
        {
            throw new ArgumentException(
                "Range start must not be later than range end.",
                nameof(fromInclusive));
        }

        // Lock only while reading the in-memory tree and copying up to `limit`
        // packet references. The caller never receives a tree node or bucket.
        lock (_sync)
        {
            var results = new List<TelemetryPacket<T>>(Math.Min(limit, _count));
            CollectRange(_root, fromUtc, toUtc, limit, results);
            return results.AsReadOnly();
        }
    }

    /// <summary>
    /// Checks height, ordering, balance, and bucket invariants without exposing
    /// internal nodes. Intended for deterministic tests and diagnostics rather
    /// than the hot ingestion path.
    /// </summary>
    public bool HasValidAvlInvariants()
    {
        lock (_sync)
        {
            int keys = 0;
            int packets = 0;
            bool valid = CheckNode(_root, null, null, ref keys, ref packets, out _);
            return valid && keys == _distinctTimestampCount && packets == _count;
        }
    }

    private Node InsertNode(Node? node, DateTimeOffset timestampUtc, TelemetryPacket<T> packet)
    {
        if (node is null)
        {
            _distinctTimestampCount++;
            return new Node(timestampUtc, packet);
        }

        int comparison = timestampUtc.CompareTo(node.TimestampUtc);
        if (comparison < 0)
        {
            node.Left = InsertNode(node.Left, timestampUtc, packet);
        }
        else if (comparison > 0)
        {
            node.Right = InsertNode(node.Right, timestampUtc, packet);
        }
        else
        {
            // The insertion order inside a timestamp bucket is stable.
            node.Packets.Add(packet);
            return node;
        }

        UpdateHeight(node);
        int balance = BalanceFactor(node);

        // LL: the left subtree's left branch became too tall.
        if (balance > 1 && timestampUtc < node.Left!.TimestampUtc)
        {
            return RotateRight(node);
        }

        // RR: the right subtree's right branch became too tall.
        if (balance < -1 && timestampUtc > node.Right!.TimestampUtc)
        {
            return RotateLeft(node);
        }

        // LR: first rotate the left child left, then the root right.
        if (balance > 1 && timestampUtc > node.Left!.TimestampUtc)
        {
            node.Left = RotateLeft(node.Left);
            return RotateRight(node);
        }

        // RL: first rotate the right child right, then the root left.
        if (balance < -1 && timestampUtc < node.Right!.TimestampUtc)
        {
            node.Right = RotateRight(node.Right);
            return RotateLeft(node);
        }

        return node;
    }

    /// <summary>
    /// Performs an in-order range walk. Entire subtrees are skipped when their
    /// keys cannot lie inside the inclusive interval. A full result stops the
    /// traversal immediately instead of visiting more historical readings.
    /// </summary>
    private static void CollectRange(
        Node? node,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        List<TelemetryPacket<T>> destination)
    {
        if (node is null || destination.Count >= limit)
        {
            return;
        }

        // All keys in the left subtree are strictly smaller than this key.
        if (node.TimestampUtc > fromUtc)
        {
            CollectRange(node.Left, fromUtc, toUtc, limit, destination);
        }

        if (destination.Count >= limit)
        {
            return;
        }

        if (node.TimestampUtc >= fromUtc && node.TimestampUtc <= toUtc)
        {
            // Equal timestamps stay in the bucket's original append order.
            foreach (TelemetryPacket<T> packet in node.Packets)
            {
                if (destination.Count >= limit)
                {
                    break;
                }

                destination.Add(packet);
            }
        }

        // All keys in the right subtree are strictly larger than this key.
        if (node.TimestampUtc < toUtc && destination.Count < limit)
        {
            CollectRange(node.Right, fromUtc, toUtc, limit, destination);
        }
    }

    private static Node RotateLeft(Node root)
    {
        Node newRoot = root.Right!;
        root.Right = newRoot.Left;
        newRoot.Left = root;

        // Recalculate the old root first because it is now a child.
        UpdateHeight(root);
        UpdateHeight(newRoot);
        return newRoot;
    }

    private static Node RotateRight(Node root)
    {
        Node newRoot = root.Left!;
        root.Left = newRoot.Right;
        newRoot.Right = root;

        UpdateHeight(root);
        UpdateHeight(newRoot);
        return newRoot;
    }

    private static int GetHeight(Node? node) => node?.Height ?? 0;

    private static void UpdateHeight(Node node)
    {
        node.Height = 1 + Math.Max(GetHeight(node.Left), GetHeight(node.Right));
    }

    private static int BalanceFactor(Node node) => GetHeight(node.Left) - GetHeight(node.Right);

    private static bool CheckNode(
        Node? node,
        DateTimeOffset? exclusiveLower,
        DateTimeOffset? exclusiveUpper,
        ref int keyCount,
        ref int packetCount,
        out int actualHeight)
    {
        if (node is null)
        {
            actualHeight = 0;
            return true;
        }

        if ((exclusiveLower.HasValue && node.TimestampUtc <= exclusiveLower.Value)
            || (exclusiveUpper.HasValue && node.TimestampUtc >= exclusiveUpper.Value)
            || node.TimestampUtc.Offset != TimeSpan.Zero
            || node.Packets.Count == 0)
        {
            actualHeight = 0;
            return false;
        }

        // Every packet in this bucket must have the node's UTC instant.
        foreach (TelemetryPacket<T> packet in node.Packets)
        {
            if (packet.CapturedAtUtc.ToUniversalTime() != node.TimestampUtc)
            {
                actualHeight = 0;
                return false;
            }
        }

        if (!CheckNode(node.Left, exclusiveLower, node.TimestampUtc,
                ref keyCount, ref packetCount, out int leftHeight)
            || !CheckNode(node.Right, node.TimestampUtc, exclusiveUpper,
                ref keyCount, ref packetCount, out int rightHeight))
        {
            actualHeight = 0;
            return false;
        }

        actualHeight = 1 + Math.Max(leftHeight, rightHeight);
        keyCount++;
        packetCount += node.Packets.Count;

        return node.Height == actualHeight && Math.Abs(leftHeight - rightHeight) <= 1;
    }

    private sealed class Node
    {
        public Node(DateTimeOffset timestampUtc, TelemetryPacket<T> firstPacket)
        {
            TimestampUtc = timestampUtc;
            Packets = [firstPacket];
        }

        public DateTimeOffset TimestampUtc { get; }

        // Equal timestamps are stored together, in original insertion order.
        public List<TelemetryPacket<T>> Packets { get; }

        public Node? Left { get; set; }

        public Node? Right { get; set; }

        public int Height { get; set; } = 1;
    }
}