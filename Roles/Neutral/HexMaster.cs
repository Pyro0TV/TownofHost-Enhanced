using AmongUs.GameOptions;
using Hazel;
using System.Text;
using TOHE.Roles.Core;
using TOHE.Roles.Crewmate;
using UnityEngine;
using static TOHE.Options;
using static TOHE.Translator;

namespace TOHE.Roles.Neutral;

internal class HexMaster : RoleBase
{
    //===========================SETUP================================\\
    private const int Id = 16400;
    private static readonly HashSet<byte> playerIdList = [];
    public static bool HasEnabled => playerIdList.Any();
    
    public override CustomRoles ThisRoleBase => CustomRoles.Impostor;
    public override Custom_RoleType ThisRoleType => Custom_RoleType.NeutralKilling;
    //==================================================================\\

    private static OptionItem ModeSwitchAction;
    private static OptionItem HexesLookLikeSpells;
    private static OptionItem HasImpostorVision;

    private static readonly Dictionary<byte, bool> HexMode = [];
    private static readonly Dictionary<byte, List<byte>> HexedPlayer = [];

    private static readonly Color RoleColorHex = Utils.GetRoleColor(CustomRoles.HexMaster);
    private static readonly Color RoleColorSpell = Utils.GetRoleColor(CustomRoles.Impostor);

    private enum SwitchTrigger
    {
        TriggerKill,
        TriggerVent,
        TriggerDouble,
    };
    private static SwitchTrigger NowSwitchTrigger;

    public override void SetupCustomOption()
    {
        SetupSingleRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.HexMaster, 1, zeroOne: false);        
        ModeSwitchAction = StringOptionItem.Create(Id + 10, "WitchModeSwitchAction", EnumHelper.GetAllNames<SwitchTrigger>(), 2, TabGroup.NeutralRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.HexMaster]);
        HexesLookLikeSpells = BooleanOptionItem.Create(Id + 11, "HexesLookLikeSpells",  false, TabGroup.NeutralRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.HexMaster]);
        HasImpostorVision = BooleanOptionItem.Create(Id + 12, "ImpostorVision",  true, TabGroup.NeutralRoles, false).SetParent(CustomRoleSpawnChances[CustomRoles.HexMaster]);
    }
    public override void Init()
    {
        playerIdList.Clear();
        HexMode.Clear();
        HexedPlayer.Clear();
    }
    public override void Add(byte playerId)
    {
        playerIdList.Add(playerId);
        HexMode.Add(playerId, false);
        HexedPlayer.Add(playerId, []);
        NowSwitchTrigger = (SwitchTrigger)ModeSwitchAction.GetValue();

        var pc = Utils.GetPlayerById(playerId);
        pc.AddDoubleTrigger();

        CustomRoleManager.MarkOthers.Add(GetHexedMark);

        if (!AmongUsClient.Instance.AmHost) return;
        if (!Main.ResetCamPlayerList.Contains(playerId))
            Main.ResetCamPlayerList.Add(playerId);
    }

    private static void SendRPC(bool doHex, byte hexId, byte target = 255)
    {
        if (doHex)
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncRoleSkill, SendOption.Reliable, -1);
            writer.WritePacked(1);
            writer.Write(hexId);
            writer.Write(target);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        else
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncRoleSkill, SendOption.Reliable, -1);
            writer.WritePacked(2);
            writer.Write(hexId);
            writer.Write(HexMode[hexId]);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

        }
    }
    public void ReceiveRPC(MessageReader reader, bool doHex)
    {
        if (doHex)
        {
            var hexmaster = reader.ReadByte();
            var hexedId = reader.ReadByte();
            if (hexedId != 255)
            {
                HexedPlayer[hexmaster].Add(hexedId);
            }
            else
            {
                HexedPlayer[hexmaster].Clear();
            }
        }
        else
        {
            byte playerId = reader.ReadByte();
            HexMode[playerId] = reader.ReadBoolean();
        }
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id) => opt.SetVision(HasImpostorVision.GetBool());

    public override bool CanUseKillButton(PlayerControl pc) => true;
    public override bool CanUseImpostorVentButton(PlayerControl pc) => true;

    private static bool IsHexMode(byte playerId)
    {
        return HexMode.ContainsKey(playerId) && HexMode[playerId];
    }
    private static void SwitchHexMode(byte playerId, bool kill)
    {
        bool needSwitch = false;
        switch (NowSwitchTrigger)
        {
            case SwitchTrigger.TriggerKill:
                needSwitch = kill;
                break;
            case SwitchTrigger.TriggerVent:
                needSwitch = !kill;
                break;
        }
        if (needSwitch)
        {
            HexMode[playerId] = !HexMode[playerId];
            SendRPC(false, playerId);
            Utils.NotifyRoles(SpecifySeer: Utils.GetPlayerById(playerId));
        }
    }
    private static bool IsHexed(byte target)
    {
        foreach (var hexmaster in playerIdList)
        {
            if (HexedPlayer[hexmaster].Contains(target)) return true;
        }
        return false;
    }
    private static void SetHexed(PlayerControl killer, PlayerControl target)
    {
        if (!IsHexed(target.PlayerId))
        {
            HexedPlayer[killer.PlayerId].Add(target.PlayerId);
            SendRPC(true, killer.PlayerId, target.PlayerId);
            //キルクールの適正化
            killer.SetKillCooldown();
        }
    }
    public override void AfterMeetingTasks()
    {
        foreach (var hexmaster in playerIdList)
        {
            HexedPlayer[hexmaster].Clear();
            SendRPC(true, hexmaster);
        }
    }
    public override bool OnCheckMurderAsKiller(PlayerControl killer, PlayerControl target)
    {
        if (Medic.ProtectList.Contains(target.PlayerId)) return false;
        if (target.Is(CustomRoles.Pestilence)) return false;

        if (NowSwitchTrigger == SwitchTrigger.TriggerDouble)
        {
            return killer.CheckDoubleTrigger(target, () => { SetHexed(killer, target); });
        }
        if (!IsHexMode(killer.PlayerId))
        {
            SwitchHexMode(killer.PlayerId, true);
            //キルモードなら通常処理に戻る
            return true;
        }
        SetHexed(killer, target);

        //スペルに失敗してもスイッチ判定
        SwitchHexMode(killer.PlayerId, true);
        //キル処理終了させる
        return false;
    }
    public static void OnCheckForEndVoting(PlayerState.DeathReason deathReason, params byte[] exileIds)
    {
        if (!HasEnabled || deathReason != PlayerState.DeathReason.Vote) return;
        foreach (var id in exileIds)
        {
            if (HexedPlayer.ContainsKey(id))
                HexedPlayer[id].Clear();
        }
        var hexedIdList = new List<byte>();
        foreach (var pc in Main.AllAlivePlayerControls)
        {
            var dic = HexedPlayer.Where(x => x.Value.Contains(pc.PlayerId));
            if (!dic.Any()) continue;
            var whichId = dic.FirstOrDefault().Key;
            var hexmaster = Utils.GetPlayerById(whichId);
            if (hexmaster != null && hexmaster.IsAlive())
            {
                if (!Main.AfterMeetingDeathPlayers.ContainsKey(pc.PlayerId))
                {
                    pc.SetRealKiller(hexmaster);
                    hexedIdList.Add(pc.PlayerId);
                }
            }
            else
            {
                Main.AfterMeetingDeathPlayers.Remove(pc.PlayerId);
            }
        }
        CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Hex, [.. hexedIdList]);
        RemoveHexedPlayer();
    }
    public override void OnPlayerExiled(PlayerControl player, GameData.PlayerInfo exiled)
    {
        RemoveHexedPlayer();
    }
    private static void RemoveHexedPlayer()
    {
        foreach (var hexmaster in playerIdList)
        {
            HexedPlayer[hexmaster].Clear();
            SendRPC(true, hexmaster);
        }
    }
    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (NowSwitchTrigger is SwitchTrigger.TriggerVent)
        {
            SwitchHexMode(pc.PlayerId, false);
        }
    }
    private string GetHexedMark(PlayerControl seer, PlayerControl target = null, bool isForMeeting = false)
    {
        target ??= seer;

        if (isForMeeting && IsHexed(target.PlayerId))
        {
            if (!HexesLookLikeSpells.GetBool())
            {
                return Utils.ColorString(RoleColorHex, "乂");
            }
            else
            {
                return Utils.ColorString(RoleColorSpell, "†");
            }
        }
        return string.Empty;
    }
    public override string GetSuffix(PlayerControl hexmaster, PlayerControl seen = null, bool isMeeting = false)
    {
        if (hexmaster == null || seen == null || isMeeting || hexmaster != seen) return "";

        var str = new StringBuilder();
        if (!isMeeting)
        {
            
            str.Append($"{GetString("Mode")}:");
            if (NowSwitchTrigger == SwitchTrigger.TriggerDouble)
            {
                str.Append(GetString("HexMasterModeDouble"));
            }
            else
            {
                str.Append(IsHexMode(hexmaster.PlayerId) ? GetString("HexMasterModeHex") : GetString("HexMasterModeKill"));
            }

            return str.ToString();
        }
        return "";
    }

    public override string GetLowerText(PlayerControl hexmaster, PlayerControl seen = null, bool isForMeeting = false, bool isForHud = false)
    {
        if (hexmaster == null) return "";

        var str = new StringBuilder();
        str.Append(GetString("WitchCurrentMode"));
        if (NowSwitchTrigger == SwitchTrigger.TriggerDouble)
        {
            str.Append(GetString("HexMasterModeDouble"));
        }
        else
        {
            str.Append(IsHexMode(hexmaster.PlayerId) ? GetString("HexMasterModeHex") : GetString("HexMasterModeKill"));
        }

        return str.ToString();
    }
    
    public override void SetAbilityButtonText(HudManager hud, byte playerid)
    {
        if (IsHexMode(playerid) && NowSwitchTrigger != SwitchTrigger.TriggerDouble)
        {
            hud.KillButton.OverrideText($"{GetString("HexButtonText")}");
        }
        else
        {
            hud.KillButton.OverrideText($"{GetString("KillButtonText")}");
        }
    }
}