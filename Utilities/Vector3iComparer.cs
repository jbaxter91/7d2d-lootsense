using System.Collections.Generic;

/// <summary>
/// Equality comparer for Vector3i coordinates used by dictionaries/sets.
/// </summary>
internal sealed class Vector3iComparer : IEqualityComparer<Vector3i>
{
    /// <summary>
    /// Compares two Vector3i instances component-wise.
    /// </summary>
    public bool Equals(Vector3i x, Vector3i y) => x.x == y.x && x.y == y.y && x.z == y.z;

    /// <summary>
    /// Combines the Vector3i components into a stable hash code.
    /// </summary>
    public int GetHashCode(Vector3i obj)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + obj.x;
            hash = hash * 31 + obj.y;
            hash = hash * 31 + obj.z;
            return hash;
        }
    }
}
