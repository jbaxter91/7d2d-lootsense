using UnityEngine;

internal readonly struct LootMarker
{
    public LootMarker(Vector3i position, Mesh mesh, Vector3 pivotOffset, Vector3 localCenter, Quaternion rotation, float lastSeenTime)
    {
        Position = position;
        Mesh = mesh;
        PivotOffset = pivotOffset;
        LocalCenter = localCenter;
        Rotation = rotation;
        LastSeenTime = lastSeenTime;
    }

    public Vector3i Position { get; }
    public Mesh Mesh { get; }
    public Vector3 PivotOffset { get; }
    public Vector3 LocalCenter { get; }
    public Quaternion Rotation { get; }
    public float LastSeenTime { get; }
}
