using System.Collections.Generic;

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
        if (agentAvatarSettingDropdown != null)
        {
            agentAvatarSettingDropdown.ClearOptions();
            agentAvatarSettingDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(AgentAvatarSetting))));
        }

        if (agentInteractionStyleDropdown != null)
        {
            agentInteractionStyleDropdown.ClearOptions();
            agentInteractionStyleDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(AgentInteractionStyle))));
        }

        if (agentBasketStyleDropdown != null)
        {
            agentBasketStyleDropdown.ClearOptions();
            agentBasketStyleDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(AgentBasketStyle))));
        }

        if (scanningDifficultyDropdown != null)
        {
            scanningDifficultyDropdown.ClearOptions();
            scanningDifficultyDropdown.AddOptions(new List<string>(System.Enum.GetNames(typeof(ScanningDifficulty))));
        }
    }

    void SyncAgentSettingsDropdowns()
    {
        agentAvatarSettingDropdown?.SetValueWithoutNotify((int)DataHandler.Instance.agentAvatarSetting);
        agentInteractionStyleDropdown?.SetValueWithoutNotify((int)DataHandler.Instance.agentInteractionStyle);
        agentBasketStyleDropdown?.SetValueWithoutNotify((int)DataHandler.Instance.agentBasketStyle);
        scanningDifficultyDropdown?.SetValueWithoutNotify((int)DataHandler.Instance.scanningDifficulty);
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
