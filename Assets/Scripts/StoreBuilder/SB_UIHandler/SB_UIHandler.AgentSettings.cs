// Settings dialog: the Agent tab here, the Rendering tab in SB_UIHandler.Rendering.cs.
public partial class SB_UIHandler
{
    void OpenSettingsDialog()
    {
        storesDialog.Hide();
        SyncAgentSettings();
        SyncRenderingSettings();
        settingsDialog.Show();
    }

    void OnSettingsTabChanged(int tab)
    {
        agentTab.SetActive(tab == 0);
        renderingTab.SetActive(tab == 1);
        for (int i = 0; i < settingsTabFx.Length; i++)
            settingsTabFx[i].SetSelected(i == tab);
    }

    void BindAgentSettings()
    {
        agentAvatarSegment.onValueChanged.AddListener(index => DataHandler.Instance.agentAvatarSetting = (AgentAvatarSetting)index);
        agentInteractionSegment.onValueChanged.AddListener(index => DataHandler.Instance.agentInteractionStyle = (AgentInteractionStyle)index);
        agentBasketSegment.onValueChanged.AddListener(index => DataHandler.Instance.agentBasketStyle = (AgentBasketStyle)index);
        scanningDifficultySegment.onValueChanged.AddListener(index => DataHandler.Instance.scanningDifficulty = (ScanningDifficulty)index);
    }

    void SyncAgentSettings()
    {
        DataHandler data = DataHandler.Instance;
        agentAvatarSegment.SetValueWithoutNotify((int)data.agentAvatarSetting);
        agentInteractionSegment.SetValueWithoutNotify((int)data.agentInteractionStyle);
        agentBasketSegment.SetValueWithoutNotify((int)data.agentBasketStyle);
        scanningDifficultySegment.SetValueWithoutNotify((int)data.scanningDifficulty);
    }
}
