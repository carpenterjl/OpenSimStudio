namespace OpenSim.Core.Numerics;

/// <summary>
/// Static 3D kd-tree over a point set. Built once, then queried for nearest-neighbour
/// and radius searches (node picking, vertex welding, duplicate detection).
/// Query results are indices into the original point list.
/// </summary>
public sealed class KdTree
{
    private readonly Vector3D[] _points;
    private readonly int[] _indices;   // permutation of original indices, laid out as an implicit tree

    public KdTree(IReadOnlyList<Vector3D> points)
    {
        _points = points.ToArray();
        _indices = new int[_points.Length];
        for (int i = 0; i < _indices.Length; i++) _indices[i] = i;
        if (_points.Length > 0)
            Build(0, _points.Length - 1, 0);
    }

    public int Count => _points.Length;

    private void Build(int lo, int hi, int depth)
    {
        if (lo >= hi) return;
        int axis = depth % 3;
        int mid = (lo + hi) / 2;
        SelectMedian(lo, hi, mid, axis);
        Build(lo, mid - 1, depth + 1);
        Build(mid + 1, hi, depth + 1);
    }

    /// <summary>Quickselect: partitions _indices[lo..hi] so the median lands at position k.</summary>
    private void SelectMedian(int lo, int hi, int k, int axis)
    {
        while (lo < hi)
        {
            double pivot = _points[_indices[(lo + hi) / 2]][axis];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (_points[_indices[i]][axis] < pivot) i++;
                while (_points[_indices[j]][axis] > pivot) j--;
                if (i <= j)
                {
                    (_indices[i], _indices[j]) = (_indices[j], _indices[i]);
                    i++; j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return;
        }
    }

    /// <summary>Returns the index of the point closest to <paramref name="query"/>, or -1 if the tree is empty.</summary>
    public int NearestNeighbor(Vector3D query)
    {
        if (_points.Length == 0) return -1;
        int best = -1;
        double bestDistSq = double.PositiveInfinity;
        Nearest(query, 0, _points.Length - 1, 0, ref best, ref bestDistSq);
        return best;
    }

    private void Nearest(Vector3D query, int lo, int hi, int depth, ref int best, ref double bestDistSq)
    {
        if (lo > hi) return;
        int mid = (lo + hi) / 2;
        int idx = _indices[mid];
        double d = Vector3D.DistanceSquared(query, _points[idx]);
        if (d < bestDistSq)
        {
            bestDistSq = d;
            best = idx;
        }

        int axis = depth % 3;
        double delta = query[axis] - _points[idx][axis];
        (int nearLo, int nearHi, int farLo, int farHi) = delta <= 0
            ? (lo, mid - 1, mid + 1, hi)
            : (mid + 1, hi, lo, mid - 1);

        Nearest(query, nearLo, nearHi, depth + 1, ref best, ref bestDistSq);
        if (delta * delta < bestDistSq)
            Nearest(query, farLo, farHi, depth + 1, ref best, ref bestDistSq);
    }

    /// <summary>Returns indices of all points within <paramref name="radius"/> of <paramref name="query"/>.</summary>
    public List<int> RadiusSearch(Vector3D query, double radius)
    {
        var result = new List<int>();
        if (_points.Length > 0)
            Radius(query, radius * radius, 0, _points.Length - 1, 0, result);
        return result;
    }

    private void Radius(Vector3D query, double radiusSq, int lo, int hi, int depth, List<int> result)
    {
        if (lo > hi) return;
        int mid = (lo + hi) / 2;
        int idx = _indices[mid];
        if (Vector3D.DistanceSquared(query, _points[idx]) <= radiusSq)
            result.Add(idx);

        int axis = depth % 3;
        double delta = query[axis] - _points[idx][axis];
        if (delta <= 0 || delta * delta <= radiusSq)
            Radius(query, radiusSq, lo, mid - 1, depth + 1, result);
        if (delta >= 0 || delta * delta <= radiusSq)
            Radius(query, radiusSq, mid + 1, hi, depth + 1, result);
    }
}
