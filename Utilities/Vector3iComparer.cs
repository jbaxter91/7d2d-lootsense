using System.Collections.Generic;

internal sealed class Vector3iComparer : IEqualityComparer<Vector3i>
{
    public bool Equals(Vector3i x, Vector3i y) => x.x == y.x && x.y == y.y && x.z == y.z;

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
