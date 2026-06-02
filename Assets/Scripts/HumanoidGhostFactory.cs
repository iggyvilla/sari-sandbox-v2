using UnityEngine;

public static class HumanoidGhostFactory
{
    public static HumanoidGhostFollower Spawn(GameObject ghostPrefab, AgentControllerBase authority)
    {
        if (ghostPrefab == null || authority == null) return null;

        Transform movementRoot = authority.MovementRoot;
        Transform view = authority.ViewTransform;
        GameObject humanoidGhost = Object.Instantiate(
            ghostPrefab,
            movementRoot.position,
            Quaternion.Euler(0f, view.eulerAngles.y, 0f));

        IKAgentController humanoidController =
            humanoidGhost.GetComponentInChildren<IKAgentController>(true);
        if (humanoidController == null)
        {
            Debug.LogError("The IK humanoid ghost prefab is missing an IKAgentController.");
            Object.Destroy(humanoidGhost);
            return null;
        }

        HumanoidGhostFollower follower =
            humanoidGhost.GetComponent<HumanoidGhostFollower>() ??
            humanoidGhost.AddComponent<HumanoidGhostFollower>();
        follower.Bind(authority, humanoidController);

        Camera ownerCamera = authority.GetComponentInChildren<Camera>(true);
        if (ownerCamera != null)
        {
            HumanoidGhostCameraVisibility visibility =
                ownerCamera.GetComponent<HumanoidGhostCameraVisibility>() ??
                ownerCamera.gameObject.AddComponent<HumanoidGhostCameraVisibility>();
            visibility.Bind(follower);
        }

        return follower;
    }
}
