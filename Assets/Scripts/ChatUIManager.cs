using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ChatUIManager : MonoBehaviour
{
    public static ChatUIManager Instance { get; private set; }

    [SerializeField] private TextMeshProUGUI chatText;
    [SerializeField] private RectTransform chatContent;
    [SerializeField] private ScrollRect chatScrollRect;
    [SerializeField] private float newLineMargin;

    public TMP_InputField chatInput;
    public GameObject chatMenu;

    public string ChatLog { get; private set; } = string.Empty;

    private bool listeningForEnter;
    private float minimumContentHeight;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (chatContent != null)
            minimumContentHeight = chatContent.rect.height;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Slash))
            EnableChatUI();

        if (listeningForEnter && Input.GetKeyDown(KeyCode.Return))
            SubmitChat();
    }

    void EnableChatUI()
    {
        chatMenu.SetActive(!chatMenu.activeSelf);
    }

    public void Log(string message)
    {
        ChatLog += $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n";

        if (chatText == null || chatContent == null || chatScrollRect == null)
        {
            Debug.LogError($"{nameof(ChatUIManager)} is missing one or more chat scroll references.");
            return;
        }

        RectTransform previousRectTransform = chatText.rectTransform;
        TextMeshProUGUI newChatText = Instantiate(chatText, chatContent);
        RectTransform newRectTransform = newChatText.rectTransform;

        newChatText.text = message;
        newChatText.ForceMeshUpdate();
        newRectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, newChatText.preferredHeight);

        Vector2 newPosition = newRectTransform.anchoredPosition;
        newPosition.y = previousRectTransform.anchoredPosition.y
            - previousRectTransform.pivot.y * previousRectTransform.rect.height
            - newLineMargin
            - (1f - newRectTransform.pivot.y) * newRectTransform.rect.height;
        newRectTransform.anchoredPosition = newPosition;

        float newBottom = newRectTransform.anchoredPosition.y
            - newRectTransform.pivot.y * newRectTransform.rect.height;
        float requiredContentHeight = Mathf.Max(minimumContentHeight, -newBottom);
        chatContent.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredContentHeight);

        chatText = newChatText;

        Canvas.ForceUpdateCanvases();
        chatScrollRect.verticalNormalizedPosition = 0f;
    }

    public void UserSelectedEnterField()
    {
        listeningForEnter = true;
    }

    private void SubmitChat()
    {
        string message = chatInput.text;
        if (string.IsNullOrEmpty(message)) return;

        chatInput.text = string.Empty;
        listeningForEnter = false;
        Log($"User: {message}");
    }
}
