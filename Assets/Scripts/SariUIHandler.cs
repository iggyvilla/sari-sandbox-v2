using TMPro;
using UnityEngine;

public class SariUIHandler : MonoBehaviour
{
    public static SariUIHandler Instance {get; private set;}
    [SerializeField] private TextMeshProUGUI infoText;
    [SerializeField] private TextMeshProUGUI interactionStyleText;
    private string lastItemInfo;
    
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    void Start()
    {
        if (DataHandler.Instance != null)
            UpdateInteractionStyleText(DataHandler.Instance.agentInteractionStyle);
    }

    public void UpdateInfoText(string itemInfo)
    {
        if (lastItemInfo != itemInfo)
        {
            lastItemInfo = itemInfo;
            if (infoText != null)
                infoText.text = $"looking at: {itemInfo}";
        }
    }

    public void UpdateInteractionStyleText(AgentInteractionStyle interactionStyle)
    {
        if (interactionStyleText != null)
            interactionStyleText.text = $"agent interaction style: {interactionStyle}";
    }
}
