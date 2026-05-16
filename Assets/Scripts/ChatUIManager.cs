using TMPro;
using UnityEngine;

public class ChatUIManager : MonoBehaviour
{
    public static ChatUIManager Instance { get; private set; }

    public TextMeshProUGUI chatText;
    public TMP_InputField chatInput;
    public GameObject chatMenu;

    private bool listeningForEnter;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Slash))
            chatMenu.SetActive(!chatMenu.activeSelf);

        if (listeningForEnter && Input.GetKeyDown(KeyCode.Return))
            SubmitChat();
    }

    public void Log(string message)
    {
        chatText.text += message + "\n";
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
