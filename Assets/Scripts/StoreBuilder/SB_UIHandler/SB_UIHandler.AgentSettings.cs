public partial class SB_UIHandler
{
    public void OnAgentSettingsMenuPressed()
    {
        bool open = !agentSettingsMenu.activeSelf;
        agentSettingsMenu.SetActive(open);

        if (open)
            SyncAgentSettingsDropdowns();
    }

    void PopulateAgentSettingsDropdowns()
    {
        FillEnumDropdown<AgentAvatarSetting>(agentAvatarSettingDropdown);
        FillEnumDropdown<AgentInteractionStyle>(agentInteractionStyleDropdown);
        FillEnumDropdown<AgentBasketStyle>(agentBasketStyleDropdown);
        FillEnumDropdown<ScanningDifficulty>(scanningDifficultyDropdown);
    }

    void SyncAgentSettingsDropdowns()
    {
        DataHandler data = DataHandler.Instance;
        ShelfEditGroupHandler.SetValue(agentAvatarSettingDropdown, (int)data.agentAvatarSetting);
        ShelfEditGroupHandler.SetValue(agentInteractionStyleDropdown, (int)data.agentInteractionStyle);
        ShelfEditGroupHandler.SetValue(agentBasketStyleDropdown, (int)data.agentBasketStyle);
        ShelfEditGroupHandler.SetValue(scanningDifficultyDropdown, (int)data.scanningDifficulty);
    }

    public void OnAgentAvatarSettingChanged(int index)
    {
        DataHandler.Instance.agentAvatarSetting = (AgentAvatarSetting)index;
    }

    public void OnAgentInteractionStyleChanged(int index)
    {
        DataHandler.Instance.agentInteractionStyle = (AgentInteractionStyle)index;
    }

    public void OnAgentBasketStyleChanged(int index)
    {
        DataHandler.Instance.agentBasketStyle = (AgentBasketStyle)index;
    }

    public void OnScanningDifficultyChanged(int index)
    {
        DataHandler.Instance.scanningDifficulty = (ScanningDifficulty)index;
    }
}
