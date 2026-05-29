using TMPro;
using UnityEngine;

public class SariUIHandler : MonoBehaviour
{
    public static SariUIHandler Instance {get; private set;}
    [SerializeField] private TextMeshProUGUI infoText;
    [SerializeField] private TextMeshProUGUI interactionStyleText;
    private string lastItemInfo;
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
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
        UpdateInteractionStyleText(DataHandler.Instance.agentInteractionStyle);
    }

    // Update is called once per frame
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
            interactionStyleText.text = $"agent interaction style: {interactionStyle.ToString()}";
    }
}
