using UnityEngine;

public sealed class VirtualItemBBoxRecord
{
    public int recordId;
    public string itemId;
    public string expirationDateDecalId;
    public InstanceData instanceData;
    public Vector3 bboxCenter;
    public Vector3 bboxSize;
    public Vector3 physicsSpawnPosition;
    public Quaternion spawnRotation;
    public Material bboxMaterial;
    public ItemSpawner ownerSpawner;
    public Transform ownerTransform;
    public int stackGroupId = -1;
    public Vector2Int gridCell;
    public ItemBBoxInfo activeBBoxInfo;
    public bool consumed;

    public bool IsActive => activeBBoxInfo != null;
}
