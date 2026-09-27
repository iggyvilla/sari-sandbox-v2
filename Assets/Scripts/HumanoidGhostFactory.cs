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

        HumanoidGhostFollower follower = GetOrAddComponent<HumanoidGhostFollower>(humanoidGhost);
        follower.Bind(authority, humanoidController);

        Camera ownerCamera = authority.GetComponentInChildren<Camera>(true);
        if (ownerCamera != null)
            GetOrAddComponent<HumanoidGhostCameraVisibility>(ownerCamera.gameObject).Bind(follower);

        return follower;
    }

    /// <summary>Disables every camera (untagging it) and audio listener under <paramref name="root"/>.</summary>
    public static void DisableCamerasAndListeners(GameObject root)
    {
        foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
        {
            camera.enabled = false;
            camera.tag = "Untagged";
        }

        foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
            listener.enabled = false;
    }

    // Explicit null check: `??` would skip Unity's fake-null returned by GetComponent in the Editor.
    private static T GetOrAddComponent<T>(GameObject go) where T : Component
    {
        T component = go.GetComponent<T>();
        return component != null ? component : go.AddComponent<T>();
    }
}
